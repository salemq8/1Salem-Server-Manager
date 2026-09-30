using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ServerManager.Connect.Setup;

internal enum SetupMode
{
    Install,
    Uninstall,
    NoPayload
}

/// <summary>
/// One small window: what will happen, one button, progress, and the result. English or
/// Arabic (right to left) following the Windows display language.
/// </summary>
internal sealed class SetupWindow : Window
{
    private readonly SetupMode _mode;
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 12, 0, 0) };
    private readonly ProgressBar _progress = new() { Height = 4, Margin = new Thickness(0, 18, 0, 0), IsIndeterminate = true, Visibility = Visibility.Hidden };
    private readonly Button _primary = new() { MinWidth = 140, Padding = new Thickness(16, 7, 16, 7), IsDefault = true };
    private readonly Button _secondary = new() { MinWidth = 110, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
    private bool _done;

    public SetupWindow(SetupMode mode)
    {
        _mode = mode;
        Title = T("1Salem Connect Setup", "إعداد 1Salem Connect");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FlowDirection = Program.Arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        FontFamily = new FontFamily("Segoe UI");

        var heading = new TextBlock
        {
            Text = mode == SetupMode.Uninstall ? T("Remove 1Salem Connect", "إزالة 1Salem Connect") : "1Salem Connect",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        };
        _message.Text = mode switch
        {
            SetupMode.Install => T(
                $"Installs 1Salem Connect in {ConnectInstaller.InstallRoot} and adds it to the Start menu and the desktop. It lets you join a friend's Minecraft server privately. It does not change your internet settings, VPN, DNS or proxy.",
                $"يثبّت 1Salem Connect في {ConnectInstaller.InstallRoot} ويضيفه إلى قائمة ابدأ وسطح المكتب. يتيح لك الانضمام إلى خادم ماينكرافت لصديقك بخصوصية، ولا يغيّر إعدادات الإنترنت أو VPN أو DNS أو الوكيل."),
            SetupMode.Uninstall => T(
                "Removes 1Salem Connect from this PC. Your own settings stay in your user folder.",
                "يزيل 1Salem Connect من هذا الجهاز. تبقى إعداداتك الخاصة في مجلد المستخدم."),
            _ => T(
                "1Salem Connect is installed from 1SalemConnect-Setup.exe. To remove it, use Installed apps in Windows Settings.",
                "يُثبَّت 1Salem Connect من الملف 1SalemConnect-Setup.exe. لإزالته استخدم التطبيقات المثبّتة في إعدادات Windows.")
        };

        _primary.Content = mode switch
        {
            SetupMode.Install => T("Install", "تثبيت"),
            SetupMode.Uninstall => T("Remove", "إزالة"),
            _ => T("Close", "إغلاق")
        };
        _secondary.Content = T("Cancel", "إلغاء");
        _secondary.Visibility = mode == SetupMode.NoPayload ? Visibility.Collapsed : Visibility.Visible;
        _primary.Click += async (_, _) => await PrimaryAsync();
        _secondary.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(_primary);
        buttons.Children.Add(_secondary);

        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        panel.Children.Add(heading);
        panel.Children.Add(_message);
        panel.Children.Add(_progress);
        panel.Children.Add(buttons);
        Content = panel;
    }

    private async Task PrimaryAsync()
    {
        if (_done || _mode == SetupMode.NoPayload)
        {
            if (_done && _mode == SetupMode.Install)
            {
                ConnectInstaller.LaunchInstalledApp();
            }

            Close();
            return;
        }

        _primary.IsEnabled = false;
        _secondary.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        var progress = new Progress<string>(step => _message.Text = step switch
        {
            "Copying files" => T("Copying files…", "جارٍ نسخ الملفات…"),
            "Adding shortcuts" => T("Adding shortcuts…", "جارٍ إضافة الاختصارات…"),
            "Registering with Windows" => T("Registering with Windows…", "جارٍ التسجيل في Windows…"),
            "Removing shortcuts" => T("Removing shortcuts…", "جارٍ إزالة الاختصارات…"),
            "Removing the uninstall entry" => T("Removing the entry from Installed apps…", "جارٍ الإزالة من التطبيقات المثبّتة…"),
            _ => T("Removing files…", "جارٍ إزالة الملفات…")
        });

        try
        {
            if (_mode == SetupMode.Install)
            {
                await Task.Run(() => ConnectInstaller.Install(progress));
                _message.Text = T(
                    "1Salem Connect is installed. You can open it now or later from the Start menu. Paste the invitation your friend sent you to join their server.",
                    "تم تثبيت 1Salem Connect. يمكنك فتحه الآن أو لاحقًا من قائمة ابدأ. الصق الدعوة التي أرسلها صديقك للانضمام إلى خادمه.");
                _primary.Content = T("Open 1Salem Connect", "فتح 1Salem Connect");
            }
            else
            {
                await Task.Run(() => ConnectInstaller.Uninstall(progress));
                _message.Text = T("1Salem Connect was removed.", "تمت إزالة 1Salem Connect.");
                _primary.Content = T("Close", "إغلاق");
            }

            _done = true;
            _secondary.Visibility = Visibility.Collapsed;
        }
        catch (InstallerBlockedException exception)
        {
            _message.Text = Program.Arabic
                ? "1Salem Connect يعمل الآن. أغلقه (بما في ذلك أيقونته بجوار الساعة) ثم حاول مرة أخرى."
                : exception.Message;
            _secondary.IsEnabled = true;
        }
        catch (Exception exception)
        {
            _message.Text = T("Setup could not finish: ", "تعذّر إكمال الإعداد: ") + exception.Message;
            _secondary.IsEnabled = true;
        }
        finally
        {
            _progress.Visibility = Visibility.Hidden;
            _primary.IsEnabled = true;
        }
    }

    private static string T(string english, string arabic) => Program.Arabic ? arabic : english;
}
