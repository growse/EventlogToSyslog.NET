EventlogToSyslog.NET
======================

A sensible Windows RFC5424-compliant. syslog forwarder written in .NET.

Download
=======

Current version 0.1.0.0:

https://s3-eu-west-1.amazonaws.com/eventlogtosyslog/EventlogToSyslog.NET.0.1.0.0.msi

Details
========

This is a fairly simple bit of code that aims to hook into all the Eventlogs it can find (or has permission for), listen for events, and forward them in a RFC5424-compatible way to a remove host over either TCP or UDP.

There are some known issues / implementation quirks that's worth pointing out:

1. (bug) There's currently no limit on the internal message buffer size. This isn't so much an issue for UDP, where it'll just throw messages on the wire. For TCP however, if there's no connection, it'll buffer up until it gets one. If this never happens, this will probably end up chewing through all your memory.
2. TCP transport: If a connection is lost, it'll try to reconnect after 100ms, and then backoff the rate until it's about 1 every 60s.
3. TCP transport: It tries to maintain a persistent connection. Therefore to differentiate between syslog messages, the ASCII char 10 ('\n') is appended to each message. Depending on your remote host, you might this char creeps into the message.
4. Curiously, the previous syslog RFC3164 specified that the message body MUST contain only visible characters, the latest RFC5424 specifies that the message only SHOULD NOT contain ASCII control characters. Because Eventlogs can be multiline and I have no idea how to handle these, I just strip all ASCII control characters out.
5. I send the HEADER as ASCII, a NILVALUE ('-') for PROCID, a NILVALUE for 'Structured Data', and the MSG as UTF-8 with a leading BOM. The HOSTNAME field is the MACHINENAME field of the Eventlog message. This isn't the FQDN, although according the RFC, it probably should be. More investigation needed.

Installation
============

The MSI accepts the arguments SYSLOGHOST and SYSLOGPORT to specify, at install-time, the remote host details. Without specifying these, some insane defaults are used.

`msiexec /qn /i EventlogToSyslog.NET.latest.msi SYSLOGHOST=mysysloghost.example.com SYSLOGPORT=5144`

Installation location is currently to `%ProgramFiles%\EventlogToSyslog.NET\`. I use NLog for logging, and logs are dumped into a 'logs' subfolder in this directory.

Changelog
=========

0.1.0.0: initial release. Basic functionality.
