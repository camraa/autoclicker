using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace AutoClic;

internal static class Program
{
    const string MutexName = @"Local\AutoClic";          // même nom que l'ancienne version : jamais deux en même temps
    const string ShowEventName = @"Local\AutoClic.Afficher";

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Une erreur ne doit jamais ouvrir de fenêtre qui bloque tout : on la note et on continue
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("interface", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("fatale", e.ExceptionObject as Exception);

        Paths.Init(args);

        using var mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Déjà lancé : on demande juste à l'autre d'afficher son menu
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var other))
            {
                using (other) other.Set();
            }
            else
            {
                MessageBox.Show("Une ancienne version d'Auto Clic tourne encore.\n\n" +
                                "Quitte-la (clic droit sur son icône près de l'horloge > Quitter), puis relance.",
                                "Auto Clic", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        Log.Info($"Démarrage d'Auto Clic {Application.ProductVersion} " +
                 $"(dossier : {Paths.DataDir}, écran : {Screen.PrimaryScreen.Bounds.Width}x{Screen.PrimaryScreen.Bounds.Height})");
        try
        {
            Application.Run(new TrayApp(showEvent));
        }
        catch (Exception ex)
        {
            Log.Error("fatale", ex);
        }
        GC.KeepAlive(mutex);
    }
}
