using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StockClient.App.Services;

namespace StockClient.App.Views;

/// <summary>
/// The account dialog. Three views, one visible at a time:
///   signed-in    — who + actions (修改密码 and 切换账号 expand on demand)
///   quick switch — one-click buttons for every remembered account
///   manual form  — username/password entry ("其他账户" or nothing remembered)
/// All state lives in <see cref="AccountSession"/>.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly AccountSession _session;
    private bool _manualMode;

    public LoginWindow(AccountSession session)
    {
        InitializeComponent();
        WindowDimmer.Attach(this);
        _session = session;

        UserBox.Text = session.Username ?? "";
        RememberBox.IsChecked = session.Username is null || session.Remember;
        AutoLoginBox.IsChecked = session.Username is null || session.AutoLogin;

        Refresh();
    }

    private void Refresh()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        SwitchResult.Visibility = Visibility.Collapsed;
        ChangePassPanel.Visibility = Visibility.Collapsed;

        if (_session.IsSignedIn)
        {
            SignedInPanel.Visibility = Visibility.Visible;
            SignedInText.Text = $"已登录：{_session.Username}";
            SwitchPanel.Visibility = Visibility.Collapsed;
            FormPanel.Visibility = Visibility.Collapsed;
            return;
        }

        SignedInPanel.Visibility = Visibility.Collapsed;

        var remembered = _session.RememberedUsers;
        if (remembered.Count > 0 && !_manualMode)
        {
            SwitchTitle.Text = "选择账户登录";
            BuildSavedList(excludeCurrent: false);
            SwitchPanel.Visibility = Visibility.Visible;
            FormPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            SwitchPanel.Visibility = Visibility.Collapsed;
            FormPanel.Visibility = Visibility.Visible;
            BackToListButton.Visibility =
                remembered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            OfflineFormButton.Margin = new Thickness(
                BackToListButton.Visibility == Visibility.Visible ? 8 : 0, 0, 0, 0);
            (UserBox.Text.Length > 0 ? (IInputElement)PassBox : UserBox).Focus();
        }
    }

    private void BuildSavedList(bool excludeCurrent)
    {
        SavedList.Children.Clear();

        foreach (var user in _session.RememberedUsers)
        {
            if (excludeCurrent
                && string.Equals(user, _session.Username, StringComparison.OrdinalIgnoreCase))
                continue;

            var captured = user;
            var button = new System.Windows.Controls.Button
            {
                Content = user,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(10, 4, 10, 4),
            };
            button.Click += async (_, _) => await SwitchAsync(captured);
            SavedList.Children.Add(button);
        }
    }

    private async Task SwitchAsync(string username)
    {
        SwitchResult.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA3));
        SwitchResult.Text = $"正在登录 {username}…";
        SwitchResult.Visibility = Visibility.Visible;

        var error = await _session.SwitchToAsync(username);
        if (error is null)
        {
            DialogResult = true;
            Close();
            return;
        }

        SwitchResult.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));
        SwitchResult.Text = error;

        // A dead saved password was dropped by the session — hand the user the
        // manual form with the username pre-filled.
        if (error.Contains("密码"))
        {
            _manualMode = true;
            UserBox.Text = username;
            Refresh();
        }
    }

    // ---- signed-in actions -------------------------------------------------

    private void ChangePassToggle_Click(object sender, RoutedEventArgs e)
    {
        SwitchPanel.Visibility = Visibility.Collapsed;
        ChangePassPanel.Visibility = ChangePassPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        if (ChangePassPanel.Visibility == Visibility.Visible) OldPassBox.Focus();
    }

    private void SwitchToggle_Click(object sender, RoutedEventArgs e)
    {
        ChangePassPanel.Visibility = Visibility.Collapsed;
        if (SwitchPanel.Visibility == Visibility.Visible)
        {
            SwitchPanel.Visibility = Visibility.Collapsed;
            return;
        }

        SwitchTitle.Text = "切换到其他账户";
        BuildSavedList(excludeCurrent: true);
        SwitchPanel.Visibility = Visibility.Visible;
    }

    private void OtherAccount_Click(object sender, RoutedEventArgs e)
    {
        _manualMode = true;
        SignedInPanel.Visibility = Visibility.Collapsed;
        SwitchPanel.Visibility = Visibility.Collapsed;
        UserBox.Text = "";
        PassBox.Clear();
        FormPanel.Visibility = Visibility.Visible;
        BackToListButton.Visibility = _session.RememberedUsers.Count > 0 || _session.IsSignedIn
            ? Visibility.Visible : Visibility.Collapsed;
        OfflineFormButton.Margin = new Thickness(
            BackToListButton.Visibility == Visibility.Visible ? 8 : 0, 0, 0, 0);
        UserBox.Focus();
    }

    private void BackToList_Click(object sender, RoutedEventArgs e)
    {
        _manualMode = false;
        Refresh();
    }

    /// <summary>
    /// 离线模式: an account-less local profile — no server, data stays on this
    /// machine, isolated from every real account. Reachable from any signed-out
    /// view; picking it while signed in signs out first.
    /// </summary>
    private void Offline_Click(object sender, RoutedEventArgs e)
    {
        if (_session.IsSignedIn) _session.SignOut();
        _session.EnterOfflineMode();
        DialogResult = true;
        Close();
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        _session.SignOut();
        PassBox.Clear();
        _manualMode = false;
        Refresh();
    }

    // ---- change password ---------------------------------------------------

    private async void ChangePass_Click(object sender, RoutedEventArgs e)
    {
        var oldPw = OldPassBox.Password;
        var newPw = NewPassBox.Password;

        void Result(string text, bool ok)
        {
            ChangePassResult.Text = text;
            ChangePassResult.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(ok ? "#3DD68C" : "#EF5350"));
        }

        if (oldPw.Length == 0 || newPw.Length == 0) { Result("请输入旧密码和新密码", false); return; }
        if (!StrongEnough(newPw)) { Result(PasswordRule, false); return; }
        if (newPw != NewPass2Box.Password) { Result("两次输入的新密码不一致", false); return; }

        ChangePassButton.IsEnabled = false;
        var error = await _session.ChangePasswordAsync(oldPw, newPw);
        ChangePassButton.IsEnabled = true;

        if (error is not null) { Result(error, false); return; }

        OldPassBox.Clear(); NewPassBox.Clear(); NewPass2Box.Clear();
        Result("修改成功（其他设备的登录已失效）", true);
    }

    // ---- manual login form -------------------------------------------------

    private void Remember_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoLoginBox is null) return;
        if (RememberBox.IsChecked != true) AutoLoginBox.IsChecked = false;
        AutoLoginBox.IsEnabled = RememberBox.IsChecked == true;
    }

    private void PassBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Login_Click(sender, e);
    }

    private async void Login_Click(object sender, RoutedEventArgs e) => await SubmitAsync(register: false);

    private async void Register_Click(object sender, RoutedEventArgs e) => await SubmitAsync(register: true);

    // Mirrors the server's strong_enough (server.py): ≥8, not all-digits, not a
    // common weak password. Kept client-side too so a weak password is rejected
    // before the round-trip instead of coming back as a server 400.
    public const string PasswordRule = "密码至少 8 位，且不能是纯数字或常见弱口令";
    private static readonly HashSet<string> WeakPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "12345678", "123456789", "1234567890", "qwertyui",
        "abcd1234", "admin123", "88888888", "11111111", "iloveyou",
    };

    public static bool StrongEnough(string pw) =>
        pw.Length >= 8 && !pw.All(char.IsDigit) && !WeakPasswords.Contains(pw);

    private async Task SubmitAsync(bool register)
    {
        var user = UserBox.Text.Trim();
        var pass = PassBox.Password;

        if (user.Length == 0 || pass.Length == 0)
        {
            ShowError("请输入用户名和密码");
            return;
        }

        // Only registration must satisfy the rule (a login just checks whatever
        // you set before). Catch it here so the guidance is specific.
        if (register && !StrongEnough(pass))
        {
            ShowError(PasswordRule);
            return;
        }

        LoginButton.IsEnabled = RegisterButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;

        var remember = RememberBox.IsChecked == true;
        var autoLogin = AutoLoginBox.IsChecked == true;
        var error = register
            ? await _session.RegisterAsync(user, pass, remember, autoLogin)
            : await _session.SignInAsync(user, pass, remember, autoLogin);

        LoginButton.IsEnabled = RegisterButton.IsEnabled = true;

        if (error is not null)
        {
            ShowError(error);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
