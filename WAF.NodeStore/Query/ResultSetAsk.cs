namespace WAF.Query;
public class ResultSetAsk<T>(string question, string answer, string prompt, Guid[] contextSources) {
    public string Question { get; } = question;
    public string Prompt { get; } = prompt;
    public string Answer { get; } = answer;
    public Guid[] Sources { get; } = contextSources;
}
