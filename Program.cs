using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace eventlog_to_syslog.net
{
    class Program
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly BlockingCollection<EventLogEntry> EventlogQueue = new BlockingCollection<EventLogEntry>();
        private static readonly BlockingCollection<string> DispatchQueue = new BlockingCollection<string>();
        static void Main(string[] args)
        {
            Log.Info("Starting");
            var eventlog = new EventLog { Log = "Application", EnableRaisingEvents = true };
            eventlog.EntryWritten += eventlog_EntryWritten;
            Log.Info("Hooked into Application");
            eventlog = new EventLog { Log = "System", EnableRaisingEvents = true };
            eventlog.EntryWritten += eventlog_EntryWritten;
            Log.Info("Hooked into System");
            eventlog = new EventLog { Log = "Security", EnableRaisingEvents = true };
            eventlog.EntryWritten += eventlog_EntryWritten;
            Log.Info("Hooked into Security");
            var queuelistener = new Thread(ProcessIncomingEventlog);
            var dispatchthread = new Thread(DispatchSyslogMessage);
            queuelistener.Start();
            dispatchthread.Start();

        }
        static void eventlog_EntryWritten(object sender, EntryWrittenEventArgs e)
        {
            EventlogQueue.Add(e.Entry);
        }


        private static void DispatchSyslogMessage()
        {
            var hostname = "gcsmon01.amers1.ciscloud";
            var port = 5145;
            using (var client = new TcpClient(hostname, port))
            {
                using (var stream = client.GetStream())
                {
                    while (true)
                    {
                        var msg = DispatchQueue.Take();

                        var data = Encoding.ASCII.GetBytes(string.Concat(msg, "\n"));
                        stream.Write(data, 0, data.Length);
                    }
                }
            }
        }

        static void ProcessIncomingEventlog()
        {
            while (true)
            {
                var eventlog = EventlogQueue.Take();
                var syslogstring = EventLogToSyslog(eventlog);
                Log.Info(syslogstring);
                DispatchQueue.Add(syslogstring);
            }
        }

        private static string EventLogToSyslog(EventLogEntry eventlog)
        {
            const FACILITY facility = FACILITY.LOCAL0;
            var severity = SEVERITY.EMERGENCY;
            switch (eventlog.EntryType)
            {
                case EventLogEntryType.Error:
                    severity = SEVERITY.ERROR;
                    break;
                case 0:
                case EventLogEntryType.Information:
                    severity = SEVERITY.INFORMATIONAL;
                    break;
                case EventLogEntryType.Warning:
                    severity = SEVERITY.WARNING;
                    break;
                case EventLogEntryType.FailureAudit:
                    severity = SEVERITY.NOTICE;
                    break;
                case EventLogEntryType.SuccessAudit:
                    severity = SEVERITY.NOTICE;
                    break;


            }
            var pri = (8 * (int)facility) + severity;
            const string format = "<{0}>1 {1} {2} {3} {4}";
            return string.Format(format, pri,
                                 eventlog.TimeGenerated.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffK"), eventlog.MachineName,
                                 eventlog.Source,
                                 eventlog.Message);
        }
    }
    enum FACILITY
    {
        KERNEL = 0,
        USER = 1,
        MAIL = 2,
        SYSTEM = 3,
        SECURITY = 4,
        INTERNAL = 5,
        LPT = 6,
        NEWS = 7,
        UUCP = 8,
        CLOCK = 9,
        FTP = 11,
        NTP = 12,
        AUDIT = 13,
        ALERT = 14,
        LOCAL0 = 16,
        LOCAL1 = 17,
        LOCAL2 = 18,
        LOCAL3 = 19,
        LOCAL4 = 20,
        LOCAL5 = 21,
        LOCAL6 = 22,
        LOCAL7 = 23,
    }
    enum SEVERITY
    {
        EMERGENCY = 0,
        ALERT = 1,
        CRITICAL = 2,
        ERROR = 3,
        WARNING = 4,
        NOTICE = 5,
        INFORMATIONAL = 6,
        DEBUG = 7
    }
}
