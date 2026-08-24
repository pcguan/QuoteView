using System.Windows;
using System.Windows.Input;
using StockClient.App.Services;

namespace StockClient.App.Views;

/// <summary>
/// Sign-in / register dialog. Thin: all state lives in <see cref="AccountSession"/>;
/// this window collects fields, shows errors, and reflects the signed-in state.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly AccountSession _session;

    public LoginWindow(AccountSession session)
    {
        InitializeComponent();
        _session = session;

        UserBox.Text = session.Username ?? "";
        RememberBox.IsChecked = session.Username is null || session.Remember;
        AutoLoginBox.IsChecked = session.Username is null || session.AutoLogin;

        Refresh();
        Loaded += (_, _) => (UserBox.Text.Length > 0 ? (IInputElement)PassBox : UserBox).Focus();
    }

    private void Refresh()
    {
        var signedIn = _session.IsSignedIn;
        SignedInPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        FormPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        if (signedIn) SignedInText.Text = $"已登录：{_session.Username}";
    }

    private void Remember_Changed(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent for the IsChecked="True" default,
        // before AutoLoginBox exists — bail until the tree is complete.
        if (AutoLoginBox is null) return;

        // 自动登录 without a remembered password can't survive a token expiry,
        // so the two travel together in the off direction.
        if (RememberBox.IsChecked != true) AutoLoginBox.IsChecked = false;
        AutoLoginBox.IsEnabled = RememberBox.IsChecked == true;
    }

    private void PassBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Login_Click(sender, e);
    }

    private async void Login_Click(object sender, RoutedEventArgs e) => await SubmitAsync(register: false);

    private async void Register_Click(object sender, RoutedEventArgs e) => await SubmitAsync(register: true);

    private async Task SubmitAsync(bool register)
    {
        var user = UserBox.Text.Trim();
        var pass = PassBox.Password;

        if (user.Length == 0 || pass.Length == 0)
        {
            ShowError("请输入用户名和密码");
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

    private async void ChangePass_Click(object sender, RoutedEventArgs e)
    {
        var oldPw = OldPassBox.Password;
        var newPw = NewPassBox.Password;

        void Result(string text, bool ok)
        {
            ChangePassResult.Text = text;
            ChangePassResult.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                    ok ? "#3DD68C" : "#EF5350"));
        }

        if (oldPw.Length == 0 || newPw.Length == 0) { Result("请输入旧密码和新密码", false); return; }
        if (newPw.Length < 6) { Result("新密码至少 6 位", false); return; }
        if (newPw != NewPass2Box.Password) { Result("两次输入的新密码不一致", false); return; }

        ChangePassButton.IsEnabled = false;
        var error = await _session.ChangePasswordAsync(oldPw, newPw);
        ChangePassButton.IsEnabled = true;

        if (error is not null) { Result(error, false); return; }

        OldPassBox.Clear(); NewPassBox.Clear(); NewPass2Box.Clear();
        Result("修改成功（其他设备的登录已失效）", true);
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        _session.SignOut();
        PassBox.Clear();
        Refresh();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
