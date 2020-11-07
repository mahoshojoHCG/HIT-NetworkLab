namespace Lab23
{
    public record FileHeader
    {
        public string Method { get; set; }
        public int ResultCode { get; set; }
        public string FileName { get; set; }
        public long ContentLength { get; set; }
    }
}