using System.Text.Json;
using System.Windows.Input;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Diagnostics;
using ZogmaDeck.Core.Integrations;
using ZogmaDeck.Services;

namespace ZogmaDeck.ViewModels;

/// <summary>
/// Identifiants Discord : identifiant d'application dans la configuration, secret et jetons chiffrés (DPAPI) à part,
/// jamais exportés ni journalisés.
/// </summary>
public sealed class DiscordCredentials(DeckRuntime rt) : IDiscordCredentials
{
    public string? ClientId => rt.Config.Settings.Discord.ClientId;
    public string? ClientSecret => rt.Secrets.Get(SecretStore.DiscordClientSecretKey);

    public DiscordTokens? Tokens
    {
        get
        {
            var json = rt.Secrets.Get(SecretStore.DiscordTokensKey);
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonSerializer.Deserialize<DiscordTokens>(json); }
            catch (JsonException) { return null; }
        }
        set => rt.Secrets.Set(SecretStore.DiscordTokensKey, value is null ? null : JsonSerializer.Serialize(value));
    }
}

/// <summary>Onglet « Intégrations » : TeamSpeak 3, TeamSpeak 5/6 et Discord.</summary>
public sealed class IntegrationsViewModel : ObservableObject
{
    private readonly DeckRuntime _rt;
    private string? _ts3Result, _ts6Result, _discordResult;
    private bool _ts3IsError, _ts6IsError, _discordIsError;

    public IntegrationsViewModel(DeckRuntime runtime)
    {
        _rt = runtime;
        Ts3TestCommand = new AsyncCommand(TestTs3Async);
        ClearTs3KeyCommand = new RelayCommand(() => SetSecret(SecretStore.Ts3ApiKeyKey, null));
        Ts6PairCommand = new AsyncCommand(PairTs6Async);
        Ts6ForgetCommand = new RelayCommand(() =>
        {
            SetSecret(SecretStore.Ts6ApiKeyKey, null);
            SetTs6("Autorisation oubliée. Retirez aussi ZogmaDeck dans TeamSpeak › Paramètres › Remote Apps si besoin.", false);
        });
        DiscordAuthorizeCommand = new AsyncCommand(AuthorizeDiscordAsync);
        DiscordForgetCommand = new AsyncCommand(async () =>
        {
            await _rt.Discord.ForgetAsync();
            Refresh();
            SetDiscord("Autorisation oubliée.", false);
        });
        ClearDiscordSecretCommand = new AsyncCommand(async () =>
        {
            SetSecret(SecretStore.DiscordClientSecretKey, null);
            await _rt.Discord.ForgetAsync();
            Refresh();
        });
        OpenLinkCommand = new RelayCommand(o => { if (o is string url) _ = _rt.Shell.OpenAsync(url, CancellationToken.None); });
    }

    public ICommand Ts3TestCommand { get; }
    public ICommand ClearTs3KeyCommand { get; }
    public ICommand Ts6PairCommand { get; }
    public ICommand Ts6ForgetCommand { get; }
    public ICommand DiscordAuthorizeCommand { get; }
    public ICommand DiscordForgetCommand { get; }
    public ICommand ClearDiscordSecretCommand { get; }
    public ICommand OpenLinkCommand { get; }

    private bool Has(string key) => !string.IsNullOrEmpty(_rt.Secrets.Get(key));

    public string Ts3KeyStatus => Has(SecretStore.Ts3ApiKeyKey) ? "Clé API enregistrée (chiffrée sur ce poste)." : "Aucune clé API enregistrée.";
    public string Ts6Status => Has(SecretStore.Ts6ApiKeyKey) ? "ZogmaDeck est autorisé dans TeamSpeak." : "ZogmaDeck n'est pas encore autorisé.";
    public string DiscordSecretStatus => Has(SecretStore.DiscordClientSecretKey) ? "Secret enregistré (chiffré sur ce poste)." : "Aucun secret enregistré.";
    public string DiscordStatus => Has(SecretStore.DiscordTokensKey)
        ? (_rt.Discord.IsConnected ? "Autorisé et connecté à Discord." : "Autorisé (connexion à la première commande).")
        : "Pas encore autorisé.";

    public string DiscordClientId
    {
        get => _rt.Config.Settings.Discord.ClientId ?? "";
        set
        {
            var v = (value ?? "").Trim();
            if (v == DiscordClientId) return;
            _rt.Config.Settings.Discord.ClientId = v.Length == 0 ? null : v;
            OnPropertyChanged();
            _rt.NotifySettingsChanged();
        }
    }

    public string? Ts3Result { get => _ts3Result; private set => Set(ref _ts3Result, value); }
    public bool Ts3IsError { get => _ts3IsError; private set => Set(ref _ts3IsError, value); }
    public string? Ts6Result { get => _ts6Result; private set => Set(ref _ts6Result, value); }
    public bool Ts6IsError { get => _ts6IsError; private set => Set(ref _ts6IsError, value); }
    public string? DiscordResult { get => _discordResult; private set => Set(ref _discordResult, value); }
    public bool DiscordIsError { get => _discordIsError; private set => Set(ref _discordIsError, value); }

    /// <summary>Enregistre un secret saisi dans un champ masqué (jamais affiché ni journalisé).</summary>
    public void SetSecret(string key, string? value)
    {
        _rt.Secrets.Set(key, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        Log.Info($"Secret « {key} » {(string.IsNullOrWhiteSpace(value) ? "effacé" : "enregistré")}.");
        Refresh();
    }

    public void Refresh()
    {
        foreach (var n in new[] { nameof(Ts3KeyStatus), nameof(Ts6Status), nameof(DiscordSecretStatus), nameof(DiscordStatus), nameof(DiscordClientId) })
            OnPropertyChanged(n);
    }

    private void SetTs3(string text, bool error) { Ts3Result = text; Ts3IsError = error; }
    private void SetTs6(string text, bool error) { Ts6Result = text; Ts6IsError = error; Refresh(); }
    private void SetDiscord(string text, bool error) { DiscordResult = text; DiscordIsError = error; Refresh(); }

    private async Task TestTs3Async()
    {
        SetTs3("Connexion à TeamSpeak 3…", false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            SetTs3(await _rt.Ts3.TestAsync(cts.Token), false);
        }
        catch (ActionException ex)
        {
            SetTs3(ex.Message, true);
        }
    }

    private async Task PairTs6Async()
    {
        SetTs6("Demande envoyée : acceptez ZogmaDeck dans la fenêtre de TeamSpeak (2 minutes)…", false);
        try
        {
            SetTs6(await _rt.Ts6.PairAsync(TimeSpan.FromMinutes(2), CancellationToken.None) +
                   " Associez ensuite chaque bouton de ZogmaDeck à une action dans TeamSpeak › Paramètres › Raccourcis.", false);
        }
        catch (ActionException ex)
        {
            SetTs6(ex.Message, true);
        }
        catch (OperationCanceledException)
        {
            SetTs6("Pas de réponse de TeamSpeak dans les 2 minutes.", true);
        }
    }

    private async Task AuthorizeDiscordAsync()
    {
        SetDiscord("Demande envoyée : acceptez l'autorisation dans Discord (2 minutes)…", false);
        try
        {
            SetDiscord(await _rt.Discord.AuthorizeAsync(CancellationToken.None), false);
        }
        catch (ActionException ex)
        {
            SetDiscord(ex.Message, true);
        }
        catch (OperationCanceledException)
        {
            SetDiscord("Pas de réponse de Discord à temps.", true);
        }
    }
}
