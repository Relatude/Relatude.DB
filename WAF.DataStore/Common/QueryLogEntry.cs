namespace WAF.Common {
    public struct QueryLogEntry() {
        public DateTime Timestamp { get; set; }
        public string Query { get; set; }
        public double Duration { get; set; }
        public int Count { get; set; }
    }
}
