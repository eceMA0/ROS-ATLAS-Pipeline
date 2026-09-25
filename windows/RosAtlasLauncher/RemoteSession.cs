using System.Diagnostics;
using System.Text;

namespace RosAtlasLauncher;

/// <summary>
/// One persistent SSH session that runs the whole bring-up.
///
/// The critical detail is that stderr is NOT redirected. OpenSSH writes its interactive prompts -
/// the host-key confirmation and "user@host's password:" - to stderr, and reads the answer from
/// the terminal. Capturing that stream makes the prompt invisible and the login fail with
/// "Permission denied" even when the password is correct. Leaving stderr attached to our console
/// lets the user authenticate normally, at the cost of ssh's diagnostics being interleaved rather
/// than captured - a good trade.
///
/// Stdin belongs to the remote script: first line is the sudo password, then it stays open as the
/// keep-alive. Closing it asks the robot to shut down; the script's EXIT trap does the rest, so
/// cleanup needs no second connection and survives a dropped link.
/// </summary>
public sealed class RemoteSession : IDisposable
{
    public const string ReadyMarker = "__ROSATLAS_READY__";
    public const string ErrorMarker = "__ROSATLAS_ERROR__";
    public const string LogMarker = "__ROSATLAS_LOG__";
    public const string StoppedMarker = "__ROSATLAS_STOPPED__";
    public const string PasswordPromptMarker = "__ROSATLAS_SEND_PASSWORD__";

    private readonly LauncherConfig config;
    private readonly Action<string> log;
    private readonly Process process;
    private readonly ManualResetEventSlim ready = new(false);
    private readonly ManualResetEventSlim stopped = new(false);
    private readonly ManualResetEventSlim passwordWanted = new(false);
    private volatile string? failure;
    private bool stdinClosed;

    public RemoteSession(LauncherConfig config, string script, Action<string> log)
    {
        this.config = config;
        this.log = log;

        var info = new ProcessStartInfo
        {
            FileName = "ssh",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,

            // Deliberately false - see the class comment. This is what makes password login work.
            RedirectStandardError = false,
        };

        foreach (var argument in BuildArguments(config, script))
        {
            info.ArgumentList.Add(argument);
        }

        this.process = Process.Start(info)
            ?? throw new InvalidOperationException("could not start ssh - is the OpenSSH client installed?");

        this.process.OutputDataReceived += this.OnOutput;
        this.process.BeginOutputReadLine();
    }

    private static IEnumerable<string> BuildArguments(LauncherConfig config, string script)
    {
        var arguments = new List<string>
        {
            "-p", config.Port.ToString(),
            "-o", "ConnectTimeout=10",
            "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=3",

            // No "-tt". A remote PTY makes the Windows ssh client put the shared console into raw
            // mode, which turns Ctrl+C into an ordinary keystroke, so neither the launcher nor
            // RosAtlasBridge ever receives it. A PTY also swallows stdin EOF, so closing stdin
            // could not stop the robot. Without one, EOF (a clean stop, a killed ssh, or a dropped
            // link) ends the script's read loop and its EXIT trap cleans up.
        };

        if (config.IdentityFile.Length > 0)
        {
            arguments.Add("-i");
            arguments.Add(config.IdentityFile);
            arguments.Add("-o");
            arguments.Add("IdentitiesOnly=yes");
        }

        arguments.Add($"{config.User}@{config.Host}");

        // The script is passed as a COMMAND ARGUMENT, not on stdin.
        //
        // "bash -s" would read the script from stdin - but stdin is our control channel for the
        // sudo password and the keep-alive. Sharing it means bash eats the password as if it were
        // another line of script, and the script's own "read" gets nothing.
        //
        // Base64 avoids every layer of quoting between PowerShell, ssh and the remote login shell:
        // the payload is a single token of [A-Za-z0-9+/=] that nothing can misinterpret. It is
        // decoded into a temp file and executed, leaving stdin untouched for us.
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script.ReplaceLineEndings("\n")));
        arguments.Add(
            "S=$(mktemp /tmp/rosatlas.XXXXXX.sh) && " +
            $"printf '%s' '{encoded}' | base64 -d > \"$S\" && " +

            // Remove on exit, NOT before running it: bash opens the script by name, so deleting it
            // first makes the interpreter fail with "No such file or directory".
            "trap 'rm -f \"$S\"' EXIT && " +

            // Tell the launcher the remote side is ready for the password, so it is only sent once
            // SSH authentication has finished.
            $"echo {PasswordPromptMarker} && " +
            "bash \"$S\"");

        return arguments;
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        var line = e.Data;
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        line = line.TrimEnd('\r');

        if (line.Contains(PasswordPromptMarker, StringComparison.Ordinal))
        {
            this.passwordWanted.Set();
            return;
        }

        if (line.Contains(ReadyMarker, StringComparison.Ordinal))
        {
            this.ready.Set();
            return;
        }

        if (line.Contains(StoppedMarker, StringComparison.Ordinal))
        {
            this.stopped.Set();
            return;
        }

        var errorIndex = line.IndexOf(ErrorMarker, StringComparison.Ordinal);
        if (errorIndex >= 0)
        {
            this.failure = line[(errorIndex + ErrorMarker.Length)..].Trim();

            // Unblock anyone waiting for readiness so the failure surfaces at once.
            this.ready.Set();
            return;
        }

        var logIndex = line.IndexOf(LogMarker, StringComparison.Ordinal);
        if (logIndex >= 0)
        {
            this.log(line[(logIndex + LogMarker.Length)..].Trim());
            return;
        }

        Console.WriteLine(line);
    }

    /// <summary>Sends the sudo password, then waits for the robot to report ready.</summary>
    public void Start(string sudoPassword, CancellationToken cancellationToken)
    {
        // Wait until the robot says it is about to run the script, i.e. SSH authentication (which
        // may be an interactive password prompt) has finished.
        while (!this.passwordWanted.Wait(250, cancellationToken))
        {
            if (this.process.HasExited)
            {
                throw new InvalidOperationException(
                    this.failure ?? BuildStartupFailure(this.process.ExitCode));
            }
        }

        // Only the password goes to stdin. The script itself travels as a command argument, so
        // bash never competes with us for this stream.
        this.process.StandardInput.NewLine = "\n";
        this.process.StandardInput.Write(sudoPassword + "\n");
        this.process.StandardInput.Flush();

        var timeout = TimeSpan.FromSeconds(this.config.ReadyTimeoutSeconds + 60);
        var deadline = DateTime.UtcNow + timeout;

        while (!this.ready.Wait(250, cancellationToken))
        {
            if (this.process.HasExited)
            {
                throw new InvalidOperationException(
                    this.failure ?? BuildStartupFailure(this.process.ExitCode));
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the robot did not finish starting up in time.");
            }
        }

        if (this.failure is not null)
        {
            throw new InvalidOperationException(this.failure);
        }
    }

    public bool IsAlive => !this.process.HasExited;

    /// <summary>Turns bare exit codes into something the user can act on.</summary>
    private static string BuildStartupFailure(int exitCode) => exitCode switch
    {
        255 => "SSH could not connect or authenticate. Check the address, username and password, " +
               "and try 'ssh <user>@<host>' manually first to accept the host key.",
        127 => "A command was not found on the robot. Run with --dump-script and check that bash, " +
               "base64, mktemp, ip and ros2 are all available.",
        126 => "A command on the robot could not be executed (permission denied, or /tmp mounted noexec).",
        _ => $"the SSH session ended unexpectedly (exit {exitCode}).",
    };

    /// <summary>
    /// Asks the robot to shut down by closing stdin, then waits for it to confirm. The remote
    /// EXIT trap does the killing, so this works even if the network has already gone away.
    /// </summary>
    public void Stop()
    {
        if (this.process.HasExited)
        {
            this.log("SSH session already closed; the robot's cleanup trap will have run.");
            return;
        }

        this.log("Stopping the robot-side nodes ...");
        try
        {
            if (!this.stdinClosed)
            {
                this.stdinClosed = true;
                this.process.StandardInput.Close();
            }
        }
        catch (IOException)
        {
            // Pipe already broken; the remote side is on its way down regardless.
        }

        // Wait for the script's "__ROSATLAS_STOPPED__", then for ssh itself.
        if (this.stopped.Wait(TimeSpan.FromSeconds(20)) && this.process.WaitForExit(10_000))
        {
            this.log("Robot-side nodes stopped.");
            return;
        }

        if (this.process.WaitForExit(5_000))
        {
            this.log("SSH session closed.");
            return;
        }

        this.log(
            "WARNING: the robot did not confirm shutdown. Verify with:" + Environment.NewLine +
            $"  ssh {this.config.User}@{this.config.Host} \"pgrep -a -f '[f]oxglove_bridge|[c]aninput_node'\"");

        try
        {
            this.process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // Nothing further we can do locally.
        }
    }

    public void Dispose()
    {
        this.Stop();
        this.ready.Dispose();
        this.stopped.Dispose();
        this.passwordWanted.Dispose();
        this.process.Dispose();
    }
}
