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
/// Une tâche de démarrage n'est visible que depuis l'application du manifeste qui la déclare : elle est donc déclarée
/// sur l'application principale, et le lancement à l'ouverture de session se reconnaît au type d'activation.
/// </summary>
internal static class PackagedStartupTask
{
    /// <summary>Identifiant déclaré dans packaging\msix\AppxManifest.xml (desktop:StartupTask/@TaskId).</summary>
    public const string TaskId = "ZogmaDeckStartup";

    private static readonly Guid IidAppInstanceStatics = new("9d11e77f-9ea6-47af-a6ec-46784c5ba254");
    private const int AppInstanceGetActivatedEventArgs = 7; // IAppInstanceStatics : get_RecommendedInstance, GetActivatedEventArgs…
    private const int ActivatedArgsGetKind = 6;             // IActivatedEventArgs : get_Kind, get_PreviousExecutionState…
    private const int ActivationKindStartupTask = 1020;

    /// <summary>
    /// Vrai si Windows a lancé l'application par sa tâche de démarrage (ouverture de session) : elle démarre alors
    /// discrètement dans la zone de notification, comme avec --tray. Faux en cas de doute.
    /// </summary>
    public static bool WasLaunchedByStartupTask()
    {
        var classId = IntPtr.Zero;
        var statics = IntPtr.Zero;
        var args = IntPtr.Zero;
        try
        {
            const string className = "Windows.ApplicationModel.AppInstance";
            Check(WindowsCreateString(className, className.Length, out classId));
            var iid = IidAppInstanceStatics;
            Check(RoGetActivationFactory(classId, ref iid, out statics));
            Check(Invoke<OutPtr>(statics, AppInstanceGetActivatedEventArgs)(statics, out args));
            if (args == IntPtr.Zero) return false;
            Check(Invoke<OutInt>(args, ActivatedArgsGetKind)(args, out var kind));
            return kind == ActivationKindStartupTask;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
        finally
        {
            if (args != IntPtr.Zero) Marshal.Release(args);
            if (statics != IntPtr.Zero) Marshal.Release(statics);
            if (classId != IntPtr.Zero) WindowsDeleteString(classId);
        }
    }

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
    internal static void Wait(IntPtr operation, TimeSpan? timeout = null)
    {
        var iid = IidAsyncInfo;
        Check(Marshal.QueryInterface(operation, in iid, out var info));
        try
        {
            var deadline = DateTime.UtcNow + (timeout ?? Timeout);
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

    internal static T Invoke<T>(IntPtr comObject, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    internal static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetAsyncFn(IntPtr self, IntPtr taskId, out IntPtr operation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int OutPtr(IntPtr self, out IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int OutInt(IntPtr self, out int value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int NoArgs(IntPtr self);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    internal static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    internal static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    internal static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
}
