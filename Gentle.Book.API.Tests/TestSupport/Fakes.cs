using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Gentle.Book.API.Tests.TestSupport;

/// <summary>Hangfire never actually needs to run a job in these tests — only capture that one was requested.</summary>
public class FakeBackgroundJobClient : IBackgroundJobClient
{
    public List<Job> CreatedJobs { get; } = new();

    public string Create(Job job, IState state)
    {
        CreatedJobs.Add(job);
        return Guid.NewGuid().ToString();
    }

    public bool ChangeState(string jobId, IState state, string? expectedState) => true;
}

/// <summary>Minimal IOptionsMonitor that never actually changes — sufficient for MollieClient, which is never exercised by these tests.</summary>
public class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable OnChange(Action<T, string?> listener) => NoopDisposable.Instance;

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}

public class FakeWebHostEnvironment : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "Gentle.Book.API.Tests";
    public string WebRootPath { get; set; } = Path.GetTempPath();
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = Path.GetTempPath();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
