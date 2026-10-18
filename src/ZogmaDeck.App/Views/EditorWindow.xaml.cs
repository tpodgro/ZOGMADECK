using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZogmaDeck.Helpers;
using ZogmaDeck.Services;
using ZogmaDeck.ViewModels;

namespace ZogmaDeck.Views;

/// <summary>Éditeur complet. Fermer la fenêtre la masque (réouverture instantanée, état conservé).</summary>
public partial class EditorWindow : Window
{
    private readonly EditorViewModel _vm;

    public EditorWindow(EditorViewModel vm, DeckRuntime runtime)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        SourceInitialized += (_, _) => runtime.Theme.ApplyChrome(this);
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => SaveAllSecrets(); // un mot de passe ou une clé saisis ne sont jamais perdus
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveAllSecrets();
        if (!((App)Application.Current).IsShuttingDown)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    public void ShowAndFocus()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Après la création d'un bouton, place le curseur dans son libellé.</summary>
    public void FocusLabel() => Dispatcher.BeginInvoke(() =>
    {
        LabelBox.Focus();
        LabelBox.SelectAll();
    }, System.Windows.Threading.DispatcherPriority.Input);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N && _vm.SelectedTab == 0 && _vm.SelectedProfile is { } p)
        {
            p.AddButton();
            FocusLabel();
            e.Handled = true;
        }
    }

    private void ButtonsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.SelectedProfile is not { } p) return;
        if (e.Key == Key.Delete) Execute(p.DeleteButtonCommand, e);
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D) Execute(p.DuplicateButtonCommand, e);
    }

    private void StepsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.SelectedProfile?.SelectedButton is not { } b) return;
        if (e.Key == Key.Delete) Execute(b.DeleteStepCommand, e);
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D) Execute(b.DuplicateStepCommand, e);
    }

    private static void Execute(ICommand c, KeyEventArgs e)
    {
        if (c.CanExecute(null)) c.Execute(null);
        e.Handled = true;
    }

    /// <summary>Menu des types d'actions, construit depuis le registre (les nouveaux types y apparaissent automatiquement).</summary>
    private void AddActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProfile?.SelectedButton is not { } button) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var first = true;
        foreach (var group in ButtonEditViewModel.ActionMenu)
        {
            if (!first) menu.Items.Add(new Separator());
            first = false;
            menu.Items.Add(new MenuItem { Header = group.Category, IsEnabled = false, FontWeight = FontWeights.SemiBold });
            foreach (var entry in group.Entries)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = entry.Name,
                    ToolTip = entry.Description,
                    Icon = new TextBlock { Text = entry.Glyph, FontFamily = IconCatalog.Font, FontSize = 14 },
                    Command = button.AddStepCommand,
                    CommandParameter = entry,
                });
            }
        }
        menu.IsOpen = true;
    }

    private void SoundRange_Committed(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SoundFieldViewModel sound) sound.OnRangeCommitted();
    }

    private void SavePassword_Click(object sender, RoutedEventArgs e) => SavePassword();

    // Quitter le champ (ex. clic sur « Tester la connexion ») enregistre le mot de passe saisi.
    private void ObsPassword_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SavePassword();

    private void ObsPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SavePassword();
        e.Handled = true;
    }

    private void SavePassword()
    {
        if (ObsPassword.Password.Length == 0) return;
        _vm.Obs.SetPassword(ObsPassword.Password);
        ObsPassword.Clear();
    }

    private void SaveAllSecrets()
    {
        SavePassword();
        SaveSecret(Ts3ApiKeyBox);
        SaveSecret(DiscordSecretBox);
    }

    // Champs masqués des intégrations : la clé de stockage est dans Tag ; enregistrés en quittant le champ ou avec Entrée.
    private void SecretBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveSecret((PasswordBox)sender);

    private void SecretBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveSecret((PasswordBox)sender);
        e.Handled = true;
    }

    private void SaveSecret(PasswordBox box)
    {
        if (box.Password.Length == 0 || box.Tag is not string key) return;
        _vm.Integrations.SetSecret(key, box.Password);
        box.Clear();
    }
}
