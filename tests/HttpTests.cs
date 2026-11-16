using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TorrentChecker;

public static class HttpTests
{
    sealed class Pending
    {
        public HttpRequestMessage Request;
        public byte[] Body;
        public CancellationToken Token;
        public TaskCompletionSource<HttpResponseMessage> Completion = new TaskCompletionSource<HttpResponseMessage>();
        public void Complete(string text, string marker)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(Encoding.GetEncoding(1251).GetBytes(text));
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            response.Content.Headers.ContentType.CharSet = "cp1251";
            response.Content.Headers.Add("X-Test", marker);
            Completion.SetResult(response);
        }
    }
    sealed class Handler : HttpMessageHandler
    {
        public readonly List<Pending> Calls = new List<Pending>();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var call = new Pending { Request = request, Token = token,
                Body = request.Content == null ? null : request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult() };
            Calls.Add(call);
            // Cancellation is deliberately completed by the test, after the next request succeeds.
            return call.Completion.Task;
        }
    }
    static int checks;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("HTTP: " + message);
        checks++;
    }
    static T Finish<T>(Task<T> task)
    {
        if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(5000)) throw new Exception("HTTP test timed out");
        return task.GetAwaiter().GetResult();
    }
    public static int Run()
    {
        checks = 0;
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        var http = new clsHTTP();
        var field = typeof(clsHTTP).GetField("http_request", BindingFlags.Instance | BindingFlags.NonPublic);
        ((HttpClient)field.GetValue(http)).Dispose();
        var handler = new Handler();
        using (var client = new HttpClient(handler))
        try
        {
            field.SetValue(http, client);
            var retained = new List<clsHTTP.HTTPResult>();
            // Every verb and payload mode, sequentially, on the same production instance.
            for (int i = 0; i < 4; i++)
            {
                bool post = i >= 2, binary = i % 2 == 1;
                var task = post ? http.HTTPPost("https://offline.invalid/post", new Dictionary<string,string>{{"q", "Привет +"}}, binary)
                    : http.HTTPGet("https://offline.invalid/get", new Dictionary<string,string>{{"q", "a b&"}}, binary, false);
                var call = handler.Calls.Last();
                Check(call.Request.Headers.ConnectionClose == true && call.Request.Headers.UserAgent.ToString().Length > 0, "common headers");
                Check(call.Request.Method == (post ? HttpMethod.Post : HttpMethod.Get), "request method");
                if (post)
                {
                    Check(Encoding.ASCII.GetString(call.Body).Equals("q=%cf%f0%e8%e2%e5%f2+%2b", StringComparison.OrdinalIgnoreCase), "cp1251 form body");
                    Check(call.Request.Content.Headers.ContentType.MediaType == "application/x-www-form-urlencoded", "form content type");
                    Check(call.Request.Headers.ExpectContinue == false && call.Request.Headers.Accept.Single().MediaType == "application/x-www-form-urlencoded", "POST headers");
                }
                else Check(call.Request.RequestUri.Query == "?q=a%20b%26" && !call.Request.Headers.Accept.Any() && call.Request.Headers.ExpectContinue == null, "GET query and headers");
                call.Complete("Ответ" + i, "marker" + i);
                var result = Finish(task);
                Check(result.Success && (binary ? Encoding.GetEncoding(1251).GetString(result.ResultB) : result.Result) == "Ответ" + i, "response body");
                Check(binary ? result.Result == null : result.ResultB == null, "no stale alternate payload");
                Check(retained.All(x => !Object.ReferenceEquals(x, result) && !Object.ReferenceEquals(x.Headers, result.Headers)), "independent results and headers");
                retained.Add(result);
            }
            var first = http.HTTPPost("https://offline.invalid/first", new Dictionary<string,string>{{"q", "one"}}, false);
            var firstCall = handler.Calls.Last();
            var second = http.HTTPGet("https://offline.invalid/second", null, true, false);
            var secondCall = handler.Calls.Last();
            Check(firstCall.Request.Headers.Accept.Any() && !secondCall.Request.Headers.Accept.Any(), "overlapping request headers stay isolated");
            secondCall.Complete("second", "second");
            var secondResult = Finish(second);
            firstCall.Complete("first", "first");
            var firstResult = Finish(first);
            Check(firstResult.Result == "first" && Encoding.ASCII.GetString(secondResult.ResultB) == "second", "reverse completion keeps bodies");
            Check(!Object.ReferenceEquals(firstResult, secondResult) && secondResult.Headers.GetValues("X-Test").Single() == "second", "reverse completion keeps headers");
            foreach (bool post in new[]{false, true})
            {
                var canceled = post ? http.HTTPPost("https://offline.invalid/cancel", new Dictionary<string,string>(), false)
                    : http.HTTPGet("https://offline.invalid/cancel", null, false, false);
                var canceledCall = handler.Calls.Last();
                http.Cancel();
                Check(canceledCall.Token.IsCancellationRequested, "Cancel reaches old request");
                var next = http.HTTPGet("https://offline.invalid/next", null, false, false);
                handler.Calls.Last().Complete("next", "next");
                var nextResult = Finish(next);
                canceledCall.Completion.SetCanceled();
                var canceledResult = Finish(canceled);
                Check(!canceledResult.Success && canceledResult.Result == null && canceledResult.ResultB == null && canceledResult.Headers == null, "canceled result is empty");
                Check(nextResult.Success && nextResult.Result == "next" && nextResult.Headers.GetValues("X-Test").Single() == "next" && !Object.ReferenceEquals(nextResult, canceledResult), "late cancellation leaves new result unchanged");
            }
            for (int i = 0; i < retained.Count; i++)
                Check(retained[i].Success && retained[i].Headers.GetValues("X-Test").Single() == "marker" + i && (i % 2 == 1 ? Encoding.GetEncoding(1251).GetString(retained[i].ResultB) : retained[i].Result) == "Ответ" + i, "retained result unchanged");
            var timeout = http.HTTPGet("https://offline.invalid/timeout", null, false, false);
            handler.Calls.Last().Completion.SetCanceled();
            bool timedOut = false;
            try { Finish(timeout); } catch (TimeoutException) { timedOut = true; }
            Check(timedOut, "uncanceled token maps cancellation to timeout");
            return checks;
        }
        finally
        {
            http.Cancel();
            SynchronizationContext.SetSynchronizationContext(oldContext);
        }
    }
}
