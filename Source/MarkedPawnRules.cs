namespace ContainmentFatalityReport
{
    internal static class MarkedPawnRules
    {
        internal static int NotificationPriority(NotificationColumn column)
        {
            if (column == null || column.mode == DeliveryMode.Message) return 0;
            switch (column.letterDef)
            {
                case "ThreatSmall": return 1;
                case "NeutralEvent": return 2;
                case "PositiveEvent": return 3;
                case "NegativeEvent": return 4;
                case "ThreatBig": return 5;
                default: return 2;
            }
        }
    }
}
