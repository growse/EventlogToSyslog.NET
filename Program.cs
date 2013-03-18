using System;
using System.ServiceProcess;
using NLog;

namespace EventlogToSyslog.NET
{
    class Program
    {
        static Logger _log;
        static void Main()
        {
            _log = LogManager.GetCurrentClassLogger();
#if DEBUG
            LogManager.GlobalThreshold = LogLevel.Trace;
#endif
            AppDomain.CurrentDomain.UnhandledException += CurrentDomainUnhandledException;

            if (Environment.UserInteractive)
            {
                EventlogToSyslog.Start();
                Console.CancelKeyPress += delegate
                {
                    _log.Info("Cancel Key Pressed. Shutting Down.");
                };
            }
            else
            {
                var servicesToRun = new ServiceBase[] { new EventlogToSyslog() };
                ServiceBase.Run(servicesToRun);
            }

        }
        static void CurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            _log.Error("Global Exception handler called with exectpion: {0}", e.ExceptionObject);
        }

    }
}
