using System.Runtime.InteropServices;
using System.Text;

namespace ZogmaDeck.Services;

/// <summary>
/// Identité de paquet MSIX (version Microsoft Store). Une application empaquetée ne peut pas utiliser HKCU\…\Run :
/// ses écritures de registre sont virtualisées. Le démarrage passe alors par la « tâche de démarrage » du manifeste.
/// </summary>
internal static class PackageInfo
{
    private const int AppModelErrorNoPackage = 15700;

    public static bool IsPackaged { get; } = DetectPackage();

    private static bool DetectPackage()
    {
        try
        {
            var length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);
}

/// <summary>
/// Windows.ApplicationModel.StartupTask appelé directement par COM (sans projection WinRT, qui ajouterait ~25 Mo).
/// Les interfaces et leurs IID proviennent de Windows.ApplicationModel.winmd et Windows.Foundation.winmd.
/// </summary>
internal static class PackagedStartupTask
{
    /// <summary>Identifiant déclaré dans packaging\msix\AppxManifest.xml (desktop:StartupTask/@TaskId).</summary>
    public const string TaskId = "ZogmaDeckStartup";

    public enum State { Disabled = 0, DisabledByUser = 1, Enabled = 2, DisabledByPolicy = 3, EnabledByPolicy = 4 }

    private static readonly Guid IidStartupTaskStatics = new("ee5b60bd-a148-41a7-b26e-e8b88a1e62f8");
    private static readonly Guid IidAsyncInfo = new("00000036-0000-0000-c000-000000000046");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // Emplacements dans les tables virtuelles (IInspectable occupe les entrées 0 à 5).
    private const int StaticsGetAsync = 7;      // IStartupTaskStatics : GetForCurrentPackageAsync, GetAsync
    private const int TaskRequestEnableAsync = 6; // IStartupTask : RequestEnableAsync, Disable, get_State, get_TaskId
    private const int TaskDisable = 7;
    private const int TaskGetState = 8;
    private const int OperationGetResults = 8;  // IAsyncOperation<T> : put_Completed, get_Completed, GetResults
    private const int InfoGetStatus = 7;        // IAsyncInfo : get_Id, get_Status, get_ErrorCode, Cancel, Close
    private const int InfoGetErrorCode = 8;

    public static State GetState() => WithTask(task => GetState(task));

    /// <summary>Active la tâche. Pour une application de bureau, Windows ne demande rien à l'utilisateur ; l'état retourné
    /// reste « DisabledByUser » si l'utilisateur l'a désactivée dans Paramètres › Applications › Démarrage.</summary>
    public static State RequestEnable() => WithTask(task =>
    {
        Check(Invoke<OutPtr>(task, TaskRequestEnableAsync)(task, out var op));
        try
        {
            Wait(op);
            Check(Invoke<OutInt>(op, OperationGetResults)(op, out var state));
            return (State)state;
        }
        finally
        {
            Marshal.Release(op);
        }
    });

    public static State Disable() => WithTask(task =>
    {
        Check(Invoke<NoArgs>(task, TaskDisable)(task));
        return GetState(task);
    });

    private static State GetState(IntPtr task)
    {
        Check(Invoke<OutInt>(task, TaskGetState)(task, out var state));
        return (State)state;
    }

    private static State WithTask(Func<IntPtr, State> action)
    {
        var statics = IntPtr.Zero;
        var op = IntPtr.Zero;
        var task = IntPtr.Zero;
        var classId = IntPtr.Zero;
        var taskId = IntPtr.Zero;
        try
        {
            const string className = "Windows.ApplicationModel.StartupTask";
            Check(WindowsCreateString(className, className.Length, out classId));
            var iid = IidStartupTaskStatics;
            Check(RoGetActivationFactory(classId, ref iid, out statics));
            Check(WindowsCreateString(TaskId, TaskId.Length, out taskId));
            Check(Invoke<GetAsyncFn>(statics, StaticsGetAsync)(statics, taskId, out op));
            Wait(op);
            Check(Invoke<OutPtr>(op, OperationGetResults)(op, out task));
            return action(task);
        }
        finally
        {
            if (task != IntPtr.Zero) Marshal.Release(task);
            if (op != IntPtr.Zero) Marshal.Release(op);
            if (statics != IntPtr.Zero) Marshal.Release(statics);
            if (taskId != IntPtr.Zero) WindowsDeleteString(taskId);
            if (classId != IntPtr.Zero) WindowsDeleteString(classId);
        }
    }

    /// <summary>Attend la fin d'une opération asynchrone WinRT (quelques millisecondes ; appel ponctuel, pas de scrutation continue).</summary>
    private static void Wait(IntPtr operation)
    {
        var iid = IidAsyncInfo;
        Check(Marshal.QueryInterface(operation, in iid, out var info));
        try
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (true)
            {
                Check(Invoke<OutInt>(info, InfoGetStatus)(info, out var status));
                switch (status)
                {
                    case 1: return; // Completed
                    case 2: throw new OperationCanceledException("Opération Windows annulée.");
                    case 3:
                        Check(Invoke<OutInt>(info, InfoGetErrorCode)(info, out var hr));
                        Check(hr);
                        throw new COMException("Opération Windows en échec.");
                }
                if (DateTime.UtcNow > deadline) throw new TimeoutException("La tâche de démarrage Windows ne répond pas.");
                Thread.Sleep(5);
            }
        }
        finally
        {
            Marshal.Release(info);
        }
    }

    private static T Invoke<T>(IntPtr comObject, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAsyncFn(IntPtr self, IntPtr taskId, out IntPtr operation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OutPtr(IntPtr self, out IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OutInt(IntPtr self, out int value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NoArgs(IntPtr self);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
}
