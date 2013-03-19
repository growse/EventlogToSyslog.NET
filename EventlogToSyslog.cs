using System;
using System.Configuration;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Security;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NLog;

namespace EventlogToSyslog.NET
{
    class EventlogToSyslog : ServiceBase
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        //TODO implement some sort of collection limiting
        private static readonly BlockingCollection<EventLogEntry> EventlogQueue = new BlockingCollection<EventLogEntry>();
        private static readonly BlockingCollection<byte[]> DispatchQueue = new BlockingCollection<byte[]>();
        private static readonly CancellationTokenSource Cts = new CancellationTokenSource();
        public static void Start()
        {
            Log.Info("Starting");

            foreach (var logname in EventLog.GetEventLogs())
            {
                try
                {
                    var eventlog = new EventLog { Log = logname.Log, EnableRaisingEvents = true };
                    eventlog.EntryWritten += eventlog_EntryWritten;
                    Log.Info("Hooked into {0} Eventlog", logname.Log);
                }
                catch (SecurityException ex)
                {
                    Log.ErrorException(string.Format("SecurityException thrown accessing {0}", logname), ex);
                }
            }
            var queuelistener = new Thread(ProcessIncomingEventlog);
            var dispatchthread = ConfigurationManager.AppSettings["syslogtransport"].ToLower().Equals("udp") ? new Thread(DispatchSyslogMessageUDP) : new Thread(DispatchSyslogMessageTCP);
            queuelistener.Start(Cts.Token);
            dispatchthread.Start(Cts.Token);

        }
        static void eventlog_EntryWritten(object sender, EntryWrittenEventArgs e)
        {
            EventlogQueue.Add(e.Entry);
        }

        private static void DispatchSyslogMessageUDP(object token)
        {
            var cancellationtoken = (CancellationToken)token;
            var hostname = ConfigurationManager.AppSettings["sysloghost"];
            int port;
            if (!int.TryParse(ConfigurationManager.AppSettings["syslogport"], out port))
            {
                //Default port
                port = 514;
            }
            Log.Info("Attempting initial connect to remote host");
            var client = new UdpClient();
            while (!cancellationtoken.IsCancellationRequested)
            {
                var data = DispatchQueue.Take(cancellationtoken);
                client.Send(data, data.Length, hostname, port);
            }

            client.Close();
        }

        private static void DispatchSyslogMessageTCP(object token)
        {
            var cancellationtoken = (CancellationToken)token;
            var hostname = ConfigurationManager.AppSettings["sysloghost"];
            int port;
            if (!int.TryParse(ConfigurationManager.AppSettings["syslogport"], out port))
            {
                //Default port
                port = 514;
            }
            Log.Info("Attempting initial connect to remote host");
            TcpClient client = null;
            var sleeptime = 100;
            while (client == null || !client.Connected)
            {
                try
                {
                    client = new TcpClient(hostname, port);
                }
                catch (SocketException ex)
                {
                    Log.ErrorException(string.Format("SocketException trying to reconnect to host. Pausing for {0}ms.", sleeptime), ex);
                    Thread.Sleep(sleeptime);
                    //Backoff
                    if (sleeptime < 60000)
                    {
                        sleeptime *= 2;
                    }
                }
            }
            Log.Info("Connected");
            sleeptime = 100;
            var stream = client.GetStream();
            while (!cancellationtoken.IsCancellationRequested)
            {
                var data = DispatchQueue.Take(cancellationtoken);
                try
                {
                    stream.Write(data, 0, data.Length);
                    //Because we're a persistent TCP stream, \r\n appears to be needed to say FINISHED NOW!!!
                    stream.Write(new byte[] { 13, 10 }, 0, 2);
                }
                catch (IOException ex)
                {
                    Log.ErrorException("IOException thrown trying to write to remote host", ex);
                    //Throw this back on the queue
                    DispatchQueue.Add(data);
                    while (!client.Connected)
                    {
                        try
                        {
                            client = new TcpClient(hostname, port);
                        }
                        catch (SocketException ex2)
                        {
                            Log.ErrorException(string.Format("SocketException trying to reconnect to host. Pausing for {0}ms.", sleeptime), ex2);
                            Thread.Sleep(sleeptime);
                            //Backoff
                            if (sleeptime < 60000)
                            {
                                sleeptime *= 2;
                            }
                        }
                    }
                    stream = client.GetStream();
                }
            }
            stream.Close();
            client.Close();
        }

        static void ProcessIncomingEventlog(object token)
        {
            var cancellationtoken = (CancellationToken)token;
            while (!cancellationtoken.IsCancellationRequested)
            {
                var eventlog = EventlogQueue.Take(cancellationtoken);
                var syslogmsg = EventLogToSyslog(eventlog);
                Log.Debug(Encoding.UTF8.GetString(syslogmsg));
                DispatchQueue.Add(syslogmsg);
            }
        }

        private static byte[] EventLogToSyslog(EventLogEntry eventlog)
        {
            const Facility facility = Facility.Local0;
            var severity = Severity.Emergency;
            switch (eventlog.EntryType)
            {
                case EventLogEntryType.Error:
                    severity = Severity.Error;
                    break;
                case 0:
                case EventLogEntryType.Information:
                    severity = Severity.Informational;
                    break;
                case EventLogEntryType.Warning:
                    severity = Severity.Warning;
                    break;
                case EventLogEntryType.FailureAudit:
                    severity = Severity.Notice;
                    break;
                case EventLogEntryType.SuccessAudit:
                    severity = Severity.Notice;
                    break;
            }
            var pri = (8 * (int)facility) + severity;
            //I'm reading RFC 5424 here. This has much to say.
            //HEADER = PRI VERSION SP TIMESTAMP SP HOSTNAME SP APP-NAME SP PROCID SP MSGID
            const string headerformat = "<{0}>{1} {2} {3} {4} {5} {6} - ";
            var header = string.Format(headerformat,
                pri,
                1, //Version
                eventlog.TimeGenerated.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffK"), //Timestamp
                eventlog.MachineName, //Hostname
                eventlog.Source, //App name
                "-", //ProcID - we don't know this, so NILVALUE
                eventlog.InstanceId //Msg ID
                );
            var msg = eventlog.Message;
            //Control characters aren't really allowed in syslog messages, so lets strip them.
            msg = Regex.Replace(msg, @"\p{C}+", "");            
            var headerbytes = Encoding.ASCII.GetBytes(header); //Header's got to be ASCII
            var utf8 = new UTF8Encoding(true);
            msg = string.Concat(utf8.GetString(utf8.GetPreamble()), msg);            
            var msgbytes = utf8.GetBytes(msg); //Message is UTF8
            var result = new byte[headerbytes.Length + msgbytes.Length + 3]; //3 is for the BOM, because msg is UTF-8
            Buffer.BlockCopy(headerbytes, 0, result, 0, headerbytes.Length);
            Buffer.BlockCopy(msgbytes, 0, result, headerbytes.Length, msgbytes.Length);
            return result;
        }

        public static void Halt()
        {
            Log.Info("Told to stop. Obeying.");
            Cts.Cancel();
            Environment.Exit(1);
        }
        protected override void OnStart(string[] args)
        {
            Start();
            base.OnStart(args);
        }

        protected override void OnStop()
        {
            Log.Info("Service OnStop called: Shutting Down");
            Log.Info("Attempting to obtain lock on monitor");
            Cts.Cancel();
            base.OnStop();
        }
    }
    enum Facility
    {
        Kernel = 0,
        User = 1,
        Mail = 2,
        System = 3,
        Security = 4,
        Internal = 5,
        LPT = 6,
        News = 7,
        UUCP = 8,
        Clock = 9,
        FTP = 11,
        NTP = 12,
        Audit = 13,
        Alert = 14,
        Local0 = 16,
        Local1 = 17,
        Local2 = 18,
        Local3 = 19,
        Local4 = 20,
        Local5 = 21,
        Local6 = 22,
        Local7 = 23,
    }
    enum Severity
    {
        Emergency = 0,
        Alert = 1,
        Critical = 2,
        Error = 3,
        Warning = 4,
        Notice = 5,
        Informational = 6,
        Debug = 7
    }
}
