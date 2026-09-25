namespace RosAtlasLauncher;

/// <summary>
/// Reads the robot's sudo password from the console without echoing it, and holds it for the
/// lifetime of the run so the user is asked at most once.
///
/// The password is kept in an ordinary string. Nothing stronger is warranted here: it is piped
/// straight into a process we spawn ourselves, and SecureString offers no real protection on
/// .NET Core anyway. What matters is that it never reaches a command line, a log line, or disk.
/// </summary>
public sealed class SudoPassword
{
    private readonly string user;
    private readonly string host;
    private string? cached;

    public SudoPassword(string user, string host)
    {
        this.user = user;
        this.host = host;
    }

    /// <summary>Set from an environment variable so CI or scripts can avoid the prompt.</summary>
    public static SudoPassword FromEnvironment(string user, string host)
    {
        var password = new SudoPassword(user, host);
        var fromEnvironment = Environment.GetEnvironmentVariable("ROSATLAS_SUDO_PASSWORD");
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            password.cached = fromEnvironment;
        }

        return password;
    }

    /// <summary>
    /// Asks for the sudo password before the SSH session starts, because once it is running its
    /// stdin is the control channel and no prompt can be interleaved.
    ///
    /// Blank is an acceptable answer: the remote script tries "sudo -n" first, so anyone with a
    /// NOPASSWD rule or a live sudo timestamp can just press Enter.
    /// </summary>
    public string GetForBringup()
    {
        if (this.cached is not null)
        {
            return this.cached;
        }

        if (Console.IsInputRedirected)
        {
            // No prompt is possible; let the remote "sudo -n" path decide whether that is fatal.
            return string.Empty;
        }

        Console.WriteLine($"Sudo password for {this.user}@{this.host} (needed to bring up CAN).");
        Console.Write("Press Enter to skip if sudo is passwordless: ");
        this.cached = ReadMasked();
        Console.WriteLine();
        return this.cached;
    }

    private static string ReadMasked()
    {
        var password = new System.Text.StringBuilder();
        while (true)
        {
            // intercept: true keeps the keystroke off the screen.
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    return password.ToString();

                case ConsoleKey.Backspace:
                    if (password.Length > 0)
                    {
                        password.Length--;
                    }

                    break;

                case ConsoleKey.Escape:
                    return string.Empty;

                default:
                    // Ignore control keys such as the arrows, which report '\0'.
                    if (!char.IsControl(key.KeyChar))
                    {
                        password.Append(key.KeyChar);
                    }

                    break;
            }
        }
    }
}
