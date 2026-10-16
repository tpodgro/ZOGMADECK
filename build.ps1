<#
.SYNOPSIS
    Compile, teste, publie et empaquette ZogmaDeck.

.EXAMPLE
    .\build.ps1                       # compilation + tests + exécutable autonome + installateur
    .\build.ps1 -Task test            # tests uniquement
    .\build.ps1 -Task publish         # exécutable portable dans artifacts\ZogmaDeck
    .\build.ps1 -Task installer       # installateur artifacts\ZogmaDeck-Setup-<version>.exe (à partir de la publication)
    .\build.ps1 -Task run             # lance la version Debug
    .\build.ps1 -Task msix            # paquet artifacts\ZogmaDeck-<version>-x64.msix pour le Microsoft Store (non signé : le Store le signe)

.EXAMPLE
    # Paquet MSIX signé par un certificat de test auto-signé, pour l'installer localement avant soumission au Store :
    .\build.ps1 -Task msix -MsixTestCertificate

.EXAMPLE
    # Signature Authenticode avec un certificat de signature de code installé (fichier, magasin Windows ou clé matérielle) :
    .\build.ps1 -CertThumbprint 0123456789ABCDEF0123456789ABCDEF01234567

.EXAMPLE
    # Signature par une commande externe (ex. Azure Artifact Signing via signtool + dlib) ; {file} est remplacé par le fichier :
    .\build.ps1 -SignCommand 'signtool sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib "C:\tools\Azure.CodeSigning.Dlib.dll" /dmdf "C:\tools\metadata.json" "{file}"'
#>
param(
    [ValidateSet('all', 'build', 'test', 'publish', 'installer', 'msix', 'run')]
    [string]$Task = 'all',
    [string]$Configuration = 'Release',
    # Exécutable léger (~quelques Mo) nécessitant le runtime « .NET 10 Desktop » installé sur le poste.
    [switch]$FrameworkDependent,
    # Empreinte d'un certificat de signature de code (Cert:\CurrentUser\My ou Cert:\LocalMachine\My).
    [string]$CertThumbprint,
    # Commande de signature externe ; « {file} » est remplacé par le chemin du fichier à signer.
    [string]$SignCommand,
    [string]$TimestampServer = 'http://timestamp.digicert.com',
    # MSIX : signe le paquet avec un certificat auto-signé (sujet = éditeur du manifeste), créé au besoin dans
    # Cert:\CurrentUser\My. Test local uniquement ; le paquet envoyé au Store n'a pas à être signé.
    [switch]$MsixTestCertificate
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Find-Dotnet {
    # SDK .NET 10 : dans le PATH, ou installé pour l'utilisateur par dotnet-install.ps1 (sans droits admin).
    $candidates = @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe") |
        Where-Object { $_ -and (Test-Path $_) }
    foreach ($d in $candidates) {
        if (& $d --list-sdks 2>$null | Where-Object { $_ -match '^10\.' }) { return $d }
    }
    Write-Host "Aucun SDK .NET 10 trouvé. Installation sans droits administrateur :" -ForegroundColor Yellow
    Write-Host '  Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1'
    Write-Host '  .\dotnet-install.ps1 -Channel 10.0 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"'
    Write-Host 'ou : winget install Microsoft.DotNet.SDK.10'
    exit 1
}

function Find-InnoSetup {
    $candidates = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source) | Where-Object { $_ -and (Test-Path $_) }
    return $candidates | Select-Object -First 1
}

function Find-SdkTool([string]$Name) {
    # Outils du SDK Windows (makeappx, makepri, signtool) : SDK installé, sinon paquet NuGet officiel
    # Microsoft.Windows.SDK.BuildTools téléchargé dans artifacts\tools (aucune installation système).
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $found = Get-ChildItem $kits -Directory -Filter '10.*' -ErrorAction SilentlyContinue | Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$Name" } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($found) { return $found }
    $tools = Join-Path $PSScriptRoot 'artifacts\tools\sdk-buildtools'
    if (-not (Test-Path $tools)) {
        $ver = '10.0.28000.2705'
        Write-Host "Téléchargement de Microsoft.Windows.SDK.BuildTools $ver (nuget.org)…"
        $nupkg = Join-Path $PSScriptRoot "artifacts\tools\buildtools.$ver.zip"
        New-Item -ItemType Directory -Force (Split-Path $nupkg) | Out-Null
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$ver/microsoft.windows.sdk.buildtools.$ver.nupkg" -OutFile $nupkg
        Expand-Archive $nupkg $tools -Force
        Remove-Item $nupkg
    }
    $found = Get-ChildItem $tools -Recurse -Filter $Name | Where-Object { $_.Directory.Name -eq 'x64' } | Select-Object -First 1
    if (-not $found) { throw "$Name introuvable (SDK Windows ou Microsoft.Windows.SDK.BuildTools)." }
    return $found.FullName
}

$dotnet = Find-Dotnet
$env:DOTNET_ROOT = Split-Path $dotnet
$version = ([xml](Get-Content (Join-Path $PSScriptRoot 'Directory.Build.props'))).Project.PropertyGroup.Version
Write-Host "ZogmaDeck $version — SDK $(& $dotnet --version) ($dotnet)" -ForegroundColor DarkGray

function Invoke-Step([string]$Name, [scriptblock]$Body) {
    Write-Host "`n=== $Name ===" -ForegroundColor Cyan
    & $Body
    if ($LASTEXITCODE -ne 0) { Write-Host "Échec : $Name" -ForegroundColor Red; exit $LASTEXITCODE }
}

$signing = [bool]($CertThumbprint -or $SignCommand)

function Invoke-Sign([string]$Path) {
    if (-not $signing) { return }
    Write-Host "Signature : $Path"
    if ($SignCommand) {
        $cmd = $SignCommand.Replace('{file}', $Path)
        & cmd.exe /c $cmd
        if ($LASTEXITCODE -ne 0) { throw "La commande de signature a échoué ($LASTEXITCODE)." }
    }
    else {
        $cert = Get-ChildItem "Cert:\CurrentUser\My\$CertThumbprint", "Cert:\LocalMachine\My\$CertThumbprint" -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $cert) { throw "Certificat $CertThumbprint introuvable dans les magasins Personnel (utilisateur ou ordinateur)." }
        $result = Set-AuthenticodeSignature -FilePath $Path -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $TimestampServer
        if ($result.Status -ne 'Valid') { throw "Signature invalide : $($result.Status) — $($result.StatusMessage)" }
    }
}

if ($Task -in 'all', 'build') {
    Invoke-Step 'Compilation' { & $dotnet build ZogmaDeck.slnx -c $Configuration }
}
if ($Task -in 'all', 'test') {
    Invoke-Step 'Tests' { & $dotnet test --project tests/ZogmaDeck.Tests -c $Configuration }
}
if ($Task -in 'all', 'publish') {
    $name = if ($FrameworkDependent) { 'ZogmaDeck-fd' } else { 'ZogmaDeck' }
    $out = Join-Path $PSScriptRoot "artifacts\$name"
    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    Invoke-Step "Publication (win-x64, fichier unique, autonome=$selfContained)" {
        & $dotnet publish src/ZogmaDeck.App/ZogmaDeck.App.csproj -c Release -r win-x64 -o $out -p:SelfContained=$selfContained
    }
    Get-ChildItem $out -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item
    Invoke-Sign "$out\ZogmaDeck.exe"
    $zip = Join-Path $PSScriptRoot "artifacts\$name-win-x64.zip"
    Compress-Archive -Path "$out\*" -DestinationPath $zip -Force
    $exe = Get-Item "$out\ZogmaDeck.exe"
    Write-Host ("`nExécutable : {0} ({1:N1} Mo)" -f $exe.FullName, ($exe.Length / 1MB)) -ForegroundColor Green
    Write-Host ("Archive    : {0} ({1:N1} Mo)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
}
if ($Task -in 'all', 'installer') {
    $iscc = Find-InnoSetup
    if (-not $iscc) {
        Write-Host "`nInno Setup 6 introuvable : installateur non généré. Installation sans droits administrateur :" -ForegroundColor Yellow
        Write-Host '  winget install --id JRSoftware.InnoSetup -e --scope user'
        if ($Task -eq 'installer') { exit 1 }
    }
    else {
        if (-not (Test-Path 'artifacts\ZogmaDeck\ZogmaDeck.exe')) { Write-Host "Publiez d'abord : .\build.ps1 -Task publish" -ForegroundColor Red; exit 1 }
        Invoke-Step "Installateur (Inno Setup)" { & $iscc /Q "/DAppVersion=$version" 'installer\ZogmaDeck.iss' }
        $setup = "artifacts\ZogmaDeck-Setup-$version.exe"
        Invoke-Sign (Resolve-Path $setup).Path
        Write-Host ("Installateur : {0} ({1:N1} Mo)" -f (Resolve-Path $setup).Path, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
        if (-not $signing) {
            Write-Host "Non signé : Windows SmartScreen affichera un avertissement au premier lancement (voir README, « Signature »)." -ForegroundColor Yellow
        }
    }
}
if ($Task -eq 'msix') {
    $makeappx = Find-SdkTool 'makeappx.exe'
    $makepri = Find-SdkTool 'makepri.exe'
    $work = Join-Path $PSScriptRoot 'artifacts\msix'
    $layout = Join-Path $work 'layout'
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    # Dans un paquet, les fichiers sont publiés séparément (pas d'exécutable unique) : rien n'est extrait au lancement
    # et les mises à jour du Store ne téléchargent que les blocs modifiés.
    Invoke-Step 'Publication pour MSIX (win-x64, autonome, fichiers séparés)' {
        & $dotnet publish src/ZogmaDeck.App/ZogmaDeck.App.csproj -c Release -r win-x64 -o $layout -p:SelfContained=true -p:PublishSingleFile=false
    }
    Get-ChildItem $layout -Filter *.pdb -Recurse | Remove-Item
    Invoke-Sign "$layout\ZogmaDeck.exe"

    # Manifeste (version x.y.z.0) et visuels
    $manifestXml = [xml](Get-Content 'packaging\msix\AppxManifest.xml' -Raw -Encoding UTF8)
    $manifestXml.Package.Identity.Version = "$version.0"
    $manifestXml.Save((Join-Path $layout 'AppxManifest.xml'))
    Copy-Item 'packaging\msix\Assets' (Join-Path $layout 'Assets') -Recurse
    $publisher = $manifestXml.Package.Identity.Publisher

    # Index des ressources : variantes d'échelle et de taille des visuels (resources.pri)
    Invoke-Step 'Index des ressources (makepri)' {
        & $makepri createconfig /cf "$work\priconfig.xml" /dq fr-FR /o | Out-Null
        & $makepri new /pr $layout /cf "$work\priconfig.xml" /mn "$layout\AppxManifest.xml" /of "$layout\resources.pri" /o | Out-Null
    }
    $msix = Join-Path $PSScriptRoot "artifacts\ZogmaDeck-$version-x64.msix"
    Invoke-Step 'Paquet MSIX (makeappx)' { & $makeappx pack /o /h SHA256 /d $layout /p $msix | Out-Null }

    if ($MsixTestCertificate) {
        $signtool = Find-SdkTool 'signtool.exe'
        $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
            Select-Object -First 1
        if (-not $cert) {
            $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName 'ZogmaDeck — test MSIX local' `
                -CertStoreLocation Cert:\CurrentUser\My -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(1)
        }
        Invoke-Step 'Signature de test (certificat auto-signé)' { & $signtool sign /q /fd SHA256 /sha1 $cert.Thumbprint $msix }
        $cer = Join-Path $PSScriptRoot 'artifacts\ZogmaDeck-test.cer'
        Export-Certificate -Cert $cert -FilePath $cer | Out-Null
        Write-Host "Certificat de test : $cer (à importer dans « Personnes autorisées » de l'ordinateur pour installer le paquet)" -ForegroundColor Yellow
    }
    elseif ($signing -and -not $SignCommand) {
        Write-Host "Paquet non signé : -CertThumbprint signe l'exécutable ; pour le paquet, le sujet du certificat doit être $publisher." -ForegroundColor Yellow
    }
    Write-Host ("`nPaquet MSIX : {0} ({1:N1} Mo)" -f $msix, ((Get-Item $msix).Length / 1MB)) -ForegroundColor Green
    if (-not $MsixTestCertificate) { Write-Host 'À téléverser dans Partner Center (le Store signe le paquet).' -ForegroundColor Green }
}
if ($Task -eq 'run') {
    & $dotnet run --project src/ZogmaDeck.App
}
