namespace MediaCatcher.Core.Logging;

public interface IAppLogger
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}

public sealed class NullAppLogger : IAppLogger
{
    public static readonly NullAppLogger Instance = new();

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message, Exception? exception = null)
    {
    }
}
