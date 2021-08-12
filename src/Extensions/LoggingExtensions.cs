﻿namespace WhMgr.Extensions
{
    using System;

    using Microsoft.Extensions.Logging;

    public static class LoggingExtensions
    {
        // Current log level
        private static readonly LogLevel _logLevel = Startup.Config.LogLevel;

        public static void LogTrace(this ILogger logger, string message, params object[] args)
        {
            if (!IsLogLevel(_logLevel, LogLevel.Trace))
                return;

            logger.LogTrace(message, args);
        }

        public static void LogDebug(this ILogger logger, string message, params object[] args)
        {
            if (!IsLogLevel(_logLevel, LogLevel.Debug))
                return;

            logger.LogDebug(message, args);
        }

        public static void LogError(this ILogger logger, string message, params object[] args)
        {
            if (!IsLogLevel(_logLevel, LogLevel.Error))
                return;

            logger.LogError(message, args);
        }

        public static void LogError(this ILogger logger, Exception error, string message, params object[] args)
        {
            if (!IsLogLevel(_logLevel, LogLevel.Error))
                return;

            logger.LogError(error, message, args);
        }

        public static void LogWarning(this ILogger logger, string message, params object[] args)
        {
            if (!IsLogLevel(_logLevel, LogLevel.Warning))
                return;

            logger.LogWarning(message, args);
        }

        public static void LogInformation(this ILogger logger, string message, params object[] args)
        {
            if (!IsLogLevel(_logLevel, LogLevel.Information))
                return;

            logger.LogInformation(message, args);
        }

        public static void LogCritical(this ILogger logger, string message, params object[] args)
        {
            if (!IsLogLevel(_logLevel, LogLevel.Critical))
                return;

            logger.LogCritical(message, args);
        }

        public static void LogCritical(this ILogger logger, Exception error, string message, params object[] args)
        {
            if (!IsLogLevel(_logLevel, LogLevel.Critical))
                return;

            logger.LogCritical(error, message, args);
        }

        private static bool IsLogLevel(LogLevel logLevel, LogLevel expectedLogLevel)
        {
            /*
            if (Startup.Config.LogLevel < LogLevel.Debug || Startup.Config.LogLevel == LogLevel.None)
                return;
             */
            return logLevel >= expectedLogLevel && logLevel != LogLevel.None;
        }
    }
}