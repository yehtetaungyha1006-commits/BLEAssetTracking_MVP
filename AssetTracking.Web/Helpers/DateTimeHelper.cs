using System;

namespace AssetTracking.Web.Helpers
{
    public static class DateTimeHelper
    {
        public static DateTime EnsureLocal(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Utc)
            {
                return dt.ToLocalTime();
            }
            
            double offsetHours = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalHours;
            if (offsetHours > 0)
            {
                // Positive offset (e.g., +7 Bangkok)
                // If we assume it is UTC and convert to local:
                var converted = DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
                // If it is in the future, it was already local
                if (converted > DateTime.Now.AddMinutes(5))
                {
                    return dt;
                }
                return converted;
            }
            else if (offsetHours < 0)
            {
                // Negative offset (e.g., -5 New York)
                // If the raw unspecified DateTime is in the future, it must be UTC
                if (dt > DateTime.Now.AddMinutes(5))
                {
                    return DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
                }
                return dt;
            }
            return dt;
        }

        public static string FormatLastSeen(DateTime? lastSeen)
        {
            if (!lastSeen.HasValue)
            {
                return "Never";
            }

            DateTime localLastSeen = EnsureLocal(lastSeen.Value);
            DateTime now = DateTime.Now;
            TimeSpan diff = now - localLastSeen;
            double diffSeconds = diff.TotalSeconds;

            if (diffSeconds < 0)
            {
                diffSeconds = 0;
            }

            if (diffSeconds < 10)
            {
                return "just now";
            }
            if (diffSeconds < 60)
            {
                return $"{(int)diffSeconds} sec ago";
            }
            if (diff.TotalMinutes < 60)
            {
                return $"{(int)diff.TotalMinutes} min ago";
            }
            if (diff.TotalHours < 24)
            {
                return $"{(int)diff.TotalHours} hr ago";
            }
            if (diff.TotalDays < 7)
            {
                return $"{(int)diff.TotalDays} days ago";
            }

            return localLastSeen.ToString("yyyy-MM-dd HH:mm");
        }

        public static bool IsOnline(DateTime? lastSeen)
        {
            if (!lastSeen.HasValue)
            {
                return false;
            }

            DateTime localLastSeen = EnsureLocal(lastSeen.Value);
            DateTime now = DateTime.Now;
            return (now - localLastSeen).TotalSeconds <= 30;
        }

        public static string GetBeaconDisplayStatus(DateTime? lastSeen)
        {
            return IsOnline(lastSeen) ? "Online" : "Offline";
        }
    }
}
