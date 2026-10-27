using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace Nomad.App.Infrastructure;

/// <summary>
/// Windows notifications (toasts) for an unpackaged app, without the WinRT projections (the app is NativeAOT with COM
/// interop off): the few WinRT calls are made through their vtables. The app id is registered under
/// <c>HKCU\Software\Classes\AppUserModelId</c>, which is all Windows 10 1903+ needs to show its toasts, and a click
/// opens <c>nomad-ai:</c>, a protocol that starts Nomad.ai, which then just brings the running one to the front.
/// Best effort: anything failing means no toast.
/// </summary>
public static unsafe class WindowsToast
{
    public const string AppId = "VlcekM.Nomad.ai";
    private const string Protocol = "nomad-ai";

    private static readonly Guid IidXmlDocumentIO = new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");
    private static readonly Guid IidXmlDocument = new("F7F3A506-1E87-42D6-BCFB-B8C809FA5494");
    private static readonly Guid IidToastFactory = new("04124B20-82C6-4229-B109-FD9ED4662B53");
    private static readonly Guid IidManagerStatics = new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");

    private static bool _registered;

    /// <summary>
    /// Registers the app id (name and icon) and the click-through protocol for this exe. Skipped for scripted dev runs,
    /// so a scratch build never takes over the protocol.
    /// </summary>
    public static void Register(bool protocol)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null)
            {
                return;
            }

            using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}"))
            {
                key.SetValue("DisplayName", "Nomad.ai");
                if (IconFile() is { } icon)
                {
                    key.SetValue("IconUri", icon);
                }
            }

            if (protocol)
            {
                using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Protocol}");
                root.SetValue("", "URL:Nomad.ai");
                root.SetValue("URL Protocol", "");
                using var command = root.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{exe}\" \"%1\"");
            }

            _registered = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
        }
    }

    /// <summary>Shows a toast with a title and a line of text; a click brings Nomad.ai to the front.</summary>
    public static void Show(string title, string text)
    {
        if (!_registered || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            return;
        }

        var xml = $"""
            <toast launch="{Protocol}:open" activationType="protocol">
              <visual><binding template="ToastGeneric"><text>{Escape(title)}</text><text>{Escape(text)}</text></binding></visual>
            </toast>
            """;

        nint document = 0, documentIO = 0, documentXml = 0, factory = 0, toast = 0, statics = 0, notifier = 0;
        try
        {
            // XmlDocument().LoadXml(xml)
            using (var name = new HString("Windows.Data.Xml.Dom.XmlDocument"))
            {
                Check(RoActivateInstance(name.Handle, out document));
            }

            Check(QueryInterface(document, IidXmlDocumentIO, out documentIO));
            using (var content = new HString(xml))
            {
                Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(documentIO, 6))(documentIO, content.Handle));
            }

            Check(QueryInterface(document, IidXmlDocument, out documentXml));

            // new ToastNotification(document)
            using (var name = new HString("Windows.UI.Notifications.ToastNotification"))
            {
                Check(GetFactory(name.Handle, IidToastFactory, out factory));
            }

            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(factory, 6))(factory, documentXml, &toast));

            // ToastNotificationManager.CreateToastNotifier(AppId).Show(toast)
            using (var name = new HString("Windows.UI.Notifications.ToastNotificationManager"))
            {
                Check(GetFactory(name.Handle, IidManagerStatics, out statics));
            }

            using (var id = new HString(AppId))
            {
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(statics, 7))(statics, id.Handle, &notifier));
            }

            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(notifier, 6))(notifier, toast));
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
        }
        finally
        {
            foreach (var p in new[] { notifier, statics, toast, factory, documentXml, documentIO, document })
            {
                if (p != 0)
                {
                    Marshal.Release(p);
                }
            }
        }
    }

    /// <summary>The app icon as a file (toasts and the notification settings show it).</summary>
    private static string? IconFile()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NomadData", "nomad.ico");
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var source = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Nomad.ai/Assets/nomad.ico"));
                using var file = File.Create(path);
                source.CopyTo(file);
            }

            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";

    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    private static int QueryInterface(nint instance, Guid iid, out nint result) => Marshal.QueryInterface(instance, in iid, out result);

    private static int GetFactory(nint classId, Guid iid, out nint factory) => RoGetActivationFactory(classId, in iid, out factory);

    private static void Check(int hr)
    {
        if (hr < 0)
        {
            throw new COMException("WinRT call failed", hr);
        }
    }

    [DllImport("combase.dll")]
    private static extern int RoActivateInstance(nint activatableClassId, out nint instance);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out nint hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    private readonly struct HString : IDisposable
    {
        public HString(string value) => Check(WindowsCreateString(value, value.Length, out Handle));

        public readonly nint Handle;

        public void Dispose() => WindowsDeleteString(Handle);
    }
}
