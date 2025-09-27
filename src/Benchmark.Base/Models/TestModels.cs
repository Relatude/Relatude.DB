namespace Benchmark.Base.Models;

public class TestUser {
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public TestCompany? Company { get; set; } 
    public TestDocument[] Documents { get; set; } = [];
}

public class TestCompany {
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public TestUser[] Users { get; set; } = [];
}

public class TestDocument {
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public TestUser? Author { get; set; }
}

