using System;
using System.Text;
using Microsoft.Extensions.Logging;

using Xunit.Abstractions;

namespace Http2Tests
{
    /// <summary>
    /// A logger implementation that writes to the XUnit Test output
    /// </summary>
    public class XUnitOutputLogger : ILogger
    {
        private static readonly object _lock = new object();
        private static readonly string _loglevelPadding = ": ";
        private static readonly string _newLineWithMessagePadding = Environment.NewLine + "      ";

        private ITestOutputHelper _outputHelper;
        private string _name;
        private Func<string, LogLevel, bool> _filter;
        private bool _includeScopes;
        private System.Diagnostics.Stopwatch _sw;

        [ThreadStatic]
        private static StringBuilder _logBuilder;

        public XUnitOutputLogger(
            string name, Func<string, LogLevel, bool> filter, bool includeScopes,
            ITestOutputHelper outputHelper)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            _name = name;
            _filter = filter;
            _includeScopes = includeScopes;
            _outputHelper = outputHelper;
            _sw = new System.Diagnostics.Stopwatch();
            _sw.Start();
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception exception,
            Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            if (formatter == null)
            {
                throw new ArgumentNullException(nameof(formatter));
            }

            var message = formatter(state, exception);

            if (!string.IsNullOrEmpty(message) || exception != null)
            {
                WriteMessage(logLevel, _name, eventId.Id, message, exception);
            }
        }

        public virtual void WriteMessage(
            LogLevel logLevel,
            string logName,
            int eventId,
            string message,
            Exception exception)
        {
            var logBuilder = _logBuilder;
            _logBuilder = null;

            if (logBuilder == null)
            {
                logBuilder = new StringBuilder();
            }

            // Example:
            // INFO: ConsoleApp.Program[10]
            //       Request received
            if (!string.IsNullOrEmpty(message))
            {
                logBuilder.Append(_sw.ElapsedMilliseconds);
                logBuilder.Append("  ");
                logBuilder.Append(GetLogLevelString(logLevel));
                logBuilder.Append(_loglevelPadding);
                logBuilder.Append(logName);
                logBuilder.Append("[");
                logBuilder.Append(eventId);
                logBuilder.AppendLine("]");
                if (_includeScopes)
                {
                    GetScopeInformation(logBuilder);
                }
                var len = logBuilder.Length;
                logBuilder.Append(message);
                logBuilder.Replace(
                    Environment.NewLine, _newLineWithMessagePadding,
                    len, message.Length);
            }

            // Example:
            // System.InvalidOperationException
            //    at Namespace.Class.Function() in File:line X
            if (exception != null)
            {
                // exception message
                if (!string.IsNullOrEmpty(message))
                {
                    logBuilder.AppendLine();
                }
                logBuilder.Append(exception.ToString());
            }

            if (logBuilder.Length > 0)
            {
                var logMessage = logBuilder.ToString();
                lock (_lock)
                {
                    _outputHelper.WriteLine(logMessage);
                }
            }

            logBuilder.Clear();
            if (logBuilder.Capacity > 1024)
            {
                logBuilder.Capacity = 1024;
            }
            _logBuilder = logBuilder;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            if (_filter == null) return true;
            return _filter(_name, logLevel);
        }

        public class NullDisposeable : IDisposable
        {
            public void Dispose()
            {
            }

            public static IDisposable Instance { get; } = new NullDisposeable();
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            return NullDisposeable.Instance;
        }

        private static string GetLogLevelString(LogLevel logLevel)
        {
            switch (logLevel)
            {
                case LogLevel.Trace:
                    return "trce";
                case LogLevel.Debug:
                    return "dbug";
                case LogLevel.Information:
                    return "info";
                case LogLevel.Warning:
                    return "warn";
                case LogLevel.Error:
                    return "fail";
                case LogLevel.Critical:
                    return "crit";
                default:
                    throw new ArgumentOutOfRangeException(nameof(logLevel));
            }
        }

        private void GetScopeInformation(StringBuilder builder)
        {
        }
    }
    
    public class XUnitOutputLoggerProvider : ILoggerProvider
    {
        private readonly ITestOutputHelper outputHelper;
        private int instanceId = 0;
        private static readonly bool XUnitLoggingEnabled = true;

        public XUnitOutputLoggerProvider(ITestOutputHelper outputHelper)
        {
            this.outputHelper = outputHelper;
        }

        public void Dispose()
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            if (XUnitLoggingEnabled)
            {
                var instId = System.Threading.Interlocked.Increment(ref instanceId);
                return new XUnitOutputLogger(categoryName, null, false, outputHelper);
            }
            return null;
        }
    }
}