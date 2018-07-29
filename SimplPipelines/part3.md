# Pipelines - a guided tour of the new IO API in .NET, part 3

Sorry, it has been longer than anticipated since [part 2](https://blog.marcgravell.com/2018/07/pipe-dreams-part-2.html) (also: [part 1](https://blog.marcgravell.com/2018/07/pipe-dreams-part-1.html)). A large part of the reason for that is that I've been trying to think how best to explain some of the inner guts of [`StackExchange.Redis`](https://github.com/StackExchange/StackExchange.Redis) in a way that makes it easy to understand, and is useful for someone trying to learn about "pipelines", not `StackExchange.Redis`. I've also been thiking on ways to feed more practical "pipelines" usage guidance into the mix, which was something that came up *a lot* in feedback to parts 1/2.

In the end, I decided that the best thing to do was to step back from `StackExchange.Redis`, and use a *completely different example*, but one that faces almost all of the same challenges.

So, with your kind permimssion, I'd like to deviate from our previously advertised agenda, and instead talk about a library by my colleague [David Haney](https://twitter.com/haneycodes) - [`SimplSockets`](https://github.com/haneytron/simplsockets). What I hope to convey is a range of both the *reasoning* behind prefering pipelines, but also practical guidance that the reader can directly transfer to their own IO-based needs. In particular, I hope to discuss:

- different ways to pass chunks of data between APIs
- working effectively with the array-pool
- `async`/`await` optimization in the context of libraries
- practical real-world examples of writing to and reading from pipelines
- how to connect pipelines client and server types to the network
- performance comparisons from pipelines

I'll be walking through *a lot* of code here, but I'll also be making the "real" code available for further exploration; this also includes some things I dodn't have time to cover here, such as how to host a pipelines server inside the Kestrel server.

Sound good?

## What is `SimplSockets`?

This is a network helper library designed to make it easier to implement basic client/server network comms over a socket:

- it implements a simple framing protocol to separate messages
- it allows for concurrent usage over a single client, with a message queuing mechanism
- it embeds additional data in the framing data to allow responses to be tied back to requests, to complete operations
- out-of-order and out-of-band replies are allowed - you might send requests `A`, `B`, `C` - and get the responses `A`, `C`, `D`, `B` - i.e. two of the responses came in the opposite order (presumably `B` took longer to execute), and `D` came from the server unsolicited (broadcasts, etc)
- individual messages are always complete in a single frame - there is no frame splitting
- in terms of API surface: everything is synchronous and `byte[]` based; for example the client has a `byte[] SendReceive(byte[])` method that sends a payload and blocks until the corresponding response is received, and there is a `MessageReceived` event for unsolicited messages that exposes a `byte[]`
- the server takes incoming requests via the same `MessageReceived` event, and can (if required, not always) post replies via a `Reply(byte[], ...)` method that also takes the incoming message (for pairing) - and has a `Broadcast(byte[])` method for sending a message to all clients
- there are some other nuances like heartbeats, but; that's probably enough

So; we've probably got enough there to start talking about real-world - and very common - scenarios in network code, and we can use that to start thinking about how "pipelines" makes our life easier.

Also an important point: anything I say below is not meant to be critical of `SimplSockets` - rather, it is to acknowledge that it was written when a *lot* of pieces like "pipelines" and `async`/`await` *didn't exist* - so it is more an exploration into how we *could* implement this differently with today's tools.

## First things first: we need to think about our exchange types

The first question I have here is - for received messages in particular: "how should we expose this data to consumers?". By this I mean: `SimplSockets` went with `byte[]` as the exchange type; can we improve on that? Unsurprisingly: yes. There are many approaches we can use here.

1. at one extreme, we can stick with `byte[]` - i.e. allocate a standalone copy of the data, that we can hand to the user; simple to work with, and very safe (nobody else sees that array - no risk of confusion), but it comes at the cost of allocations and copy time.
2. at the other extreme, we can use zero-copy - and stick with `ReadOnlySequence<byte>` - this means we're consuming the non-contiguous buffers *in the pipe itself*; this is *fast*, but somewhat limiting - we can't *hand that out*, because once we `Advance` the pipe: that data is going to be recycled. This might be a good option for strictly controlled server-side processing (where the data never escapes the request context)
3. as an extension of `2`, we could move the *payload* parsing code into the library (based on the live `ReadOnlySequence<byte>`), just exposing the *deconstructed* data, perhaps using custom `struct`s that map to the scenario; efficient, but requires lots more knowledge of the contents than a general message-passing API allows; this might be a good option if you can pair the library with a serializer that accepts input as `ReadOnlySequence<byte>`, though - allowing the serializer to work on the data without any copies
4. we could return a `Memory<byte>` to a copy of the data, perhaps using an oversized `byte[]` from the `ArrayPool<byte>.Shared` pool; but it isn't necessarily obvious to the consumer that they should return it to the pool (and indeed: getting a `T[]` array back from a `Memory<T>` is an advanced and "unsafe" operation - not all `Memory<T>` is based on `T[]` - so we *really* shouldn't encourage users to try)
5. we could compromise by returning something that *provides* a `Memory<byte>` (or `Span<byte>` etc), but which makes it *very obvious* via a well-known API that the user is meant to do something when they're done with it - i.e. `IDisposable` / `using` - and have the exchange-type *itself* return things to the pool when `Dispose()` is called

In the context of a general purpose messaging API, I think that `5` is a reasonable option - it means the caller *can* store the data for some period while they work with it, without jamming the pipe, while still allowing us to make good use of the array pool. And if someone forgets the `using`, it is *less efficient*, but nothing will actually explode - it just means it'll tend to run a bit more like option `1`. But: this decision of exchange types needs careful consideration for your scenario. The `StackExchange.Redis` client uses option `3`, handing out deconstructed data; I also have a fake redis *server* using the `StackExchange.Redis` framing code, which uses option `2` - never allowing live escape a request context. You need to take time in considering your exchange types, because it is basically impossible to change this later!

> As a pro tip for option `2` (using live `ReadOnlySequence<byte>` data and not letting it escape the context - zero-copy for maxiumum efficiency), one way to *guarantee* this is to wrap the buffer in a domain-specific `ref struct` before handing it to the code that needs to consume it. It is impossible to store a `ref struct`, which includes holding onto it in an `async`/`await` context, and includes basic reflection (since that requires "boxing", and you cannot "box" a `ref struct`) - so you have confidence that when the method completes, they no longer have indirect access to the data.

But, let's assume we're happy with option `5` (*for this specific scenario* - there is no general "here's the option you should use", except: not `1` if you can help it). What might that look like? It turns out that this intent is already desribed in the framework, as `System.Buffers.IMemoryOwner<T>`:

```c#
public interface IMemoryOwner<T> : IDisposable
{
    Memory<T> Memory { get; }
}
```

We can then implement this to put our leased arrays back into the array-pool when disposed, taking care to be thread-safe so that if it is disposed twice, we don't put the array into the pool twice (very bad):

```c#
private sealed class ArrayPoolOwner<T> : IMemoryOwner<T>
{
    private readonly int _length;
    private T[] _oversized;

    internal ArrayPoolOwner(T[] oversized, int length)
    {
        _length = length;
        _oversized = oversized;                
    }

    public Memory<T> Memory => new Memory<T>(GetArray(), 0, _length);

    private T[] GetArray() =>
        Interlocked.CompareExchange(ref _oversized, null, null)
        ?? throw new ObjectDisposedException(ToString());

    public void Dispose()
    {
        var arr = Interlocked.Exchange(ref _oversized, null);
        if (arr != null) ArrayPool<T>.Shared.Return(arr);
    }
}
```

The key point here is in `Dispose()`, where it swaps out the array field (using `Interlocked.Exchange`), and puts the array back into the pool. Once we've done this, subsequent calls to `.Memory` will fail, and calls to `Dispose()` will do nothing.

An imporant thing to note about the array-pool is that the arrays you get are often *oversized* (so that it can give you a larger array if it doesn't have one in exactly your size, but it has a larger one ready to go). This means we need to track the *expected* length (`_length`), and use that when constructing `.Memory`. As a side note, I wonder whether the above concept might be a worthy addition inside the framework itself, for usage directly from `ArrayPool<T>` - i.e. a method like `IMemoryOwner<T> RentOwned(int length)` alongside `T[] Rent(int minimumLength)`.

The idea here is that passing an `IMemoryOwner<T>` expresses a transfer of ownership, so a typical usage might be:

```c#
void DoSomethingWith(IMemoryOwner<byte> data)
{
    using(data)
    {
        DoTheThing(data.Memory);
    }
}
```

The caller doesn't need to know about the implementation details (array-pool, etc). Note that we still have to allocate a *small* object to represent this, but this is still hugely preferable to allocating a large `byte[]` buffer each time, for our safety.

As a caveat, we should note that a badly written consumer could store the `.Memory` somewhere, which would lead to undefined behaviour after it has been disposed; or they could use `MemoryMarshal` to get an array from the memory. If we *really needed to prevent these problems*, we could do so by implementing a custom `MemoryManager<T>` (most likely, by making `ArrayPoolOwner<T> : MemoryManager<T>`, since `MemoryManager<T> : IMemoryOwner<T>`). We could then make `.Span` fail just like `.Memory` does above, and we could prevent `MemoryMarshal` from being able to obtain the underlying array. It is almost certainly overkill here, but it is useful to know that this option exists, for more extreme scenarios.

At this point you're probably thinking "wow, Marc, you're really over-thinking this - just give them the data", but: getting the exchange types right is probably the single most important design decision you have to make, so: this bit matters!

OK, so how would we populate this? Fortunately, that is pretty simple, as `ReadOnlySequence<T>` has a very handy `CopyTo` method that does all the heavy lifting:

```c#
public static IMemoryOwner<T> Lease<T>(
    this ReadOnlySequence<T> source)
{
    if (source.IsEmpty) return Empty<T>();

    int len = checked((int)source.Length);
    var arr = ArrayPool<T>.Shared.Rent(len);
    source.CopyTo(arr);
    return new ArrayPoolOwner<T>(arr, len);
}
```

This shows how we can use `ArrayPool<T>` to obtain a (possibly oversized) array that we can use to hold a *copy* of the data; once we've copied it, we can hand the *copy* to a consumer to use however they need (and being a flat vector here makes it simple to consume), while the network code can advance the pipe and discard / re-use the buffers. When they `Dispose()` it, it goes back in the pool, and everyone is happy.

## Starting the base API

There is a *lot* of overlap in the code between a client and server; both need thread-safe mechanisms to write data, and both need some kind of read-loop to check for received data; but what *happens* is different. So - it sounds like a a base-class might be useful; let's start with a skeleton API that let's us hand in a pipe (or two: recall that an `IDuplexPipe` is actually the ends of two *different* pipes - `.Input` and `.Output`):

```c#
public abstract class SimplPipeline : IDisposable
{
    private IDuplexPipe _pipe;
    protected SimplPipeline(IDuplexPipe pipe)
        => _pipe = pipe;

    public void Dispose() => Close();
    public void Close() {/* burn the pipe*/}
}
```

The first thing we need after this is some mechanism to send a message in a thread-safe way that doesn't block the caller unduly. The way `SimplSockets` handles this (and also how `StackExchange.Redis` v1 works) is to have a *message queue* of messages that *have not yet been written*. When the caller calls `Send`, the messages is added to the queue (synchronized, etc), and will *at some point* be dequeued and written to the socket. This helps with perceived performance and can help avoid packet fragmentation in some scenarios, *but*:

- it has a lot of moving parts
- it duplicates something that "pipelines" already provides

For the latter, specifically: the pipe **is the queue**; meaning: we *already have* a buffer of data between the actual output. Adding a *second* queue is just duplicating this and retaining complexity, so: the second major design change we can make is: *throw away the unsent queue*; just write to the pipe (synchronized, etc), and let the pipe worry about the rest. One slight consequence of this is that the v1 code had a concept of prioritising messages that are expecting a reply - essentially queue-jumping. By treating the pipe as the outbound queue we *lose this ability*, but in reality this is unlikely to make a huge difference, so I'm happy to lose it. For very similar reasons, `StackExchange.Redis` v2 loses the concept of `CommandFlags.HighPriority`, which is this exact same queue-jumping idea. I'm not concerned by this.

We also need to consider the *shape* of this API, to allow a server or client to add a messagee

- we don't necessarily want to be synchronous; we don't need to block while waiting to access to write to the pipe, or while waiting for a response from the server
- we might want to expose alternate APIs for whether the caller is simply giving us memory to write (`ReadOnlyMember<byte>`), or *giving us owneship* of the data, for us to clean up when we've written it (`IMemoryOwner<byte>`)
- let's assume that write and read are decoupled - we don't want to worry about the issues of response messages here

So; putting that together, I quite like:

```c#
protected async ValueTask WriteAsync(
    IMemoryOwner<byte> payload, int messageId)
{
    using (payload)
    {
        await WriteAsync(payload.Memory, messageId);
    }
}
protected ValueTask WriteAsync(
    ReadOnlyMemory<byte> payload, int messageId);
```

Here we're giving the caller the conveninence of passing us either an `IMemoryOwner<byte>` (which we then dispose correctly), or a `ReadOnlyMemory<byte>` if they don't need to convery ownership.

The `ValueTask` makes sense because a write *to a pipe* is often synchronous; we *probably* won't be contested for the single-writer access, and the only async part of writing to a pipe is flushing *if the pipe is backed up* (flushing is very often always synchronous). The `messageId` is the additional metadata in the frame header that lets us pair replies later. We'll worry about what it *is* in a bit.

## Writes and wrongs

So; let's implement that. The first thing we need is guaranteed single-writer access. It would be tempting to use a `lock`, but `lock` *doesn't play well with `async`* ([even if you don't screw it up](https://twitter.com/marcgravell/status/1023176337652109312)). Because the flush *may* be async, the continuation could come back on another thread, so we need an `async`-compatible locking primitive; `SemaphoreSlim` should suffice.

Next, I'm going to go off on one of my wild tangents. Premise:

> In general, application code should be optimized for readability; library code should be optimized for performance.

You may or may not agree with this, but it is the general guidance that I code by. What I mean by this is that *library* code tends to have a *single focused purpose*, often being maintained by someone whose experience may be "deep but not necessarily wide"; your mind is focusing on that one area, and it is OK to go to bizarre lengths to optimize the code. Conversely, *application* code tends to involve a lot more plumbing of *different* concepts - "wide but not necessarily deep" (the depth being hidden in the various libraries). Application code is often has more complex and unpredictabe interactions, so the focus should be on maintainable and "obviously right".

Basically, my point here is that I tend to focus a lot on optimizations that you wouldn't normally put into application code, because *I know from experience and extensive benchmarking* that they *really matter*. So... I'm going to do some things that might look odd, and I want you to take that journey with me.

Let's start with the "obviously right" implementation:

```c#
private readonly SemaphoreSlim _singleWriter
    = new SemaphoreSlim(1);
protected async ValueTask WriteAsync(
    ReadOnlyMemory<byte> payload, int messageId)
{
    await _singleWriter.WaitAsync();
    try
    {
        WriteFrameHeader(writer, payload.Length, messageId);
        await writer.WriteAsync(payload);
    }
    finally
    {
        _singleWriter.Release();
    }
}
```

This `await`s single-writer access to the pipe, writes the frame header using `WriteFrameHeader` (which we'll show in a bit), then drops the `payload` using the framework-provided `WriteAsync` method, noting that this includes the `FlushAsync` as well. There's nothing *wrong* with this code, but... it does involve unnecessary state machine plumbing in the **most likely case** - i.e. where everything completes synchronously (the writer is not contested, and the pipe is not backed up). We can tweak this code by asking:

- can I get the single-writer access uncontested?
- was the flush synchronous?

Consider, instead - making the method we just wrote `private` and renaming it to `WriteAsyncSlowPath`, and adding a **non-`async`** method instead:

```c#
protected ValueTask WriteAsync(
    ReadOnlyMemory<byte> payload, int messageId)
{
    // try to get the conch; if not, switch to async
    if (!_singleWriter.Wait(0))
        return WriteAsyncSlowPath(payload, messageId);
    bool release = true;
    try
    {
        WriteFrameHeader(writer, payload.Length, messageId);
        var write = writer.WriteAsync(payload);
        if (write.IsCompletedSuccessfully) return default;
        release = false;
        return AwaitFlushAndRelease(write);
    }
    finally
    {
        if (release) _singleWriter.Release();
    }
}
async ValueTask AwaitFlushAndRelease(
    ValueTask<FlushResult> flush)
{
    try { await flush; }
    finally { _singleWriter.Release(); }
}
```

The `Wait(0)` returns `true` *if and only if* we can take the semaphore synchronously without delay. If we can't: all bets are off, just switch to the `async` version. Note once you've gone `async`, there's no point doing any more of these "hot path" checks - you've already built a state machine (and probably boxed it): the meal is already paid for, so you might as well sit and eat.

However, if we *do* get the semaphore for free, we can continue and do our *writing* for free. The header is synchronous *anyway*, so our next decision is: did the *flush* complete synchronously? If it did (`IsCompletedSuccessfully`), *we're done* - away we go (`return default;`). Otherwise, we'll need to `await` the flush. Now, we can't do that from our non-`async` method, but we can write a *separate* method (`AwaitFlushAndRelease`) that takes our incomplete flush, and `await`s it. In particular, note that we only want the semaphore to be released *after* the flush has completed, hence the `Release()` in our helper method. This is also why we set `release` to `false` in the calling method, so it doesn't get released prematurely.

We can apply similar techniques to *most* `async` operations if we know they're going to *often* be synchronous, and it is a pattern you may wish to consider. Emphasis: it doesn't help you *at all* if the result is usually or always *genuinely* synchronous - so: don't over-apply it.

---

Right; so - how do we write the header? What *is* the header? `SimplSockets` defines the header to be 8 bytes composed of two little-endian 32-bit integers. The first 4 bytes contains the payload length in bytes; the second 4 bytes is the `messageId` used to correlating requests and responses. Writing this is remarkably simple:

```c#
void WriteFrameHeader(PipeWriter writer, int length, int messageId)
{
    var span = writer.GetSpan(8);
    BinaryPrimitives.WriteInt32LittleEndian(
        span, length);
    BinaryPrimitives.WriteInt32LittleEndian(
        span.Slice(4), messageId);
    writer.Advance(8);
}
```

You can ask a `PipeWriter` for "reasonable" sized buffers with confidence, and `8` bytes is certainly a reasonable size. The helpful framework-provided `BinaryPrimitives` type provides explicit-endian tools, perfect for network code. The first call writes `length` to the first 4 bytes of the span. After that, we need to `Slice` the span so that the second call writes to the *next* 4 bytes - and finally we call `Advance(8)` which commits our header to the pipe *without* flushing it. Normally, you might have to write lots of pieces manually, then call `FlushAsync` explicitly, but this particular protocol is a good fit for simply calling `WriteAsync` on the pipe to attach the payload, which *includes* the flush. So; putting those pieces together, we've successfully written our message to the pipe.

## Using that from a client

We have a `WriteAsync` method in the base class; now let's add a concrete client class and start hooking pieces together. Consider:

```c#
public class SimplPipelineClient : SimplPipeline
{
    public async Task<IMemoryOwner<byte>> SendReciveAsync(ReadOnlyMemory<byte> message)
    {
        var tcs = new TaskCompletionSource<IMemoryOwner<byte>>();
        int messageId;
        lock (_awaitingResponses)
        {
            messageId = ++_nextMessageId;
            if (messageId == 0) messageId = 1;
            _awaitingResponses.Add(messageId, tcs);
        }
        await WriteAsync(message, messageId);
        return await tcs.Task;
    }
    public async Task<IMemoryOwner<byte>> SendReciveAsync(IMemoryOwner<byte> message)
    {
        using (message)
        {
            return await SendReciveAsync(message.Memory);
        }
    }
}
```

where `_awaitingResponses` is a dictionary of `int` message-ids to `TaskCompletionSource<IMemoryOwner<byte>>`. This code invents a new `messageId` (avoiding zero, which we'll use as a sentinel value), and creates a `TaskCompletionSource<T>` to represent our in-progress operation. Since this definitely will involve network access, there's no benefit in exposing it as `ValueTask<T>`, so this works well. Once we've added our placeholder for catching the reply we write our message (always do book-keeping *first*, to avoid race conditions). Finally, expose the incomplete task to the caller. 

 Note that I've implemented this the "obvious" way, but we can optimize this like we did previously, by checking if `WriteAsync` completed synchronously and simply `return`ing the `tcs.Task` without `await`ing it. Note also that `SimplSockets` used the *calling thread-id* as the message-id; this works fine in a blocking scenario, but it isn't viable when we're using `async` - but: the number is opaque to the "other end" *anyway* - all it has to do is return the same number.

 ## Programmed to receive

 That's pretty-much it for write; next we need to think about receive. As mentioned in the previous posts, there's almost always a receive *loop* - especially if we need to support out-of-band and out-of-order messages (so: we can't just read one frame immediately after writing). A basic read loop can be approximated by:

 ```c#
protected async Task StartReceiveLoopAsync(
    CancellationToken cancellationToken = default)
{
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var readResult = await reader.ReadAsync(cancellationToken);
            if (readResult.IsCanceled) break;

            var buffer = readResult.Buffer;

            var makingProgress = false;
            while (TryParseFrame(ref buffer, out var payload, out var messageId))
            {
                makingProgress = true;
                await OnReceiveAsync(payload, messageId);
            }
            reader.AdvanceTo(buffer.Start, buffer.End);
            if (!makingProgress && readResult.IsCompleted) break;
        }
        try { reader.Complete(); } catch { }
    }
    catch (Exception ex)
    {
        try { reader.Complete(ex); } catch { }
    }
}
protected abstract ValueTask OnReceiveAsync(
    ReadOnlySequence<byte> payload, int messageId);
```

Note: since we are *bound* to have an `async` delay at some point (probably immediately), we might as well just jump straight to an "obvoious" `async` implementation - we'll gain nothing from trying to be clever here. Key points to observe:

- we get data from the pipe (note that we *might* want to also consider `TryRead` here, but only if we are making progress - otherwise we couold find ourselves in a hot loop)
- read (`TryParseFrame`) and process (`OnReceiveAsync`) as many frames as we can
- advance the reader to report our progress, noting that `TryParseFrame` will have updated `buffer.Start`, and since we're actively reading as many frames as we can, it is true to say that we've "inspected" to `buffer.End`
- keep in mind that the pipelines code is dealing with all the back-buffer concerns re data that we haven't consumed yet (usually a significant amount of code repeated in lots of libraries)
- check for exit conditions - if we aren't progressing and the pipe won't get any more data, we're done
- report when we've finished reading - through success or failure

Unsurprisingly, `TryParseFrame` is largely the reverse of `WriteAsync`:

```c#
private bool TryParseFrame(
    ref ReadOnlySequence<byte> input,
    out ReadOnlySequence<byte> payload, out int messageId)
{
    if (input.Length < 8)
    {   // not enough data for the header
        payload = default;
        messageId = default;
        return false;
    }

    int length;
    if (input.First.Length >= 8)
    {   // already 8 bytes in the first segment
        length = ParseFrameHeader(
            input.First.Span, out messageId);
    }
    else
    {   // copy 8 bytes into a local span
        Span<byte> local = stackalloc byte[8];
        input.Slice(0, 8).CopyTo(local);
        length = ParseFrameHeader(
            local, out messageId);
    }

    // do we have the "length" bytes?
    if (input.Length < length + 8)
    {
        payload = default;
        return false;
    }

    // success!
    payload = input.Slice(8, length);
    input = input.Slice(payload.End);
    return true;
}
```

First we check whether we have enough data for the frame header (8 bytes); if we don't have that - we certainly don't have a frame. Once we know we have enough bytes for the frame header, we can parse it out to find the payload length. This is a little subtle, because we need to recall that `ReadOnlySequence<byte>` can be *discontiguous* multiple buffers. Since we're only talking about 8 bytes, the simplest thing to do is:

- check whether the *first segment* has 8 bytes; if so, parse from that
- otherwise, `stackalloc` a span (note that this doesn't need `unsafe`), copy 8 bytes from `input` into that, and parse *from there*.

Once we know how much payload we're expecting, we can check whether we *have that too*; if we don't: cede back to the read loop. But if we do:

- our *actual payload* is the `length` bytes *after* the header - i.e. `input.Slice(8, length)`
- we want to update `input` by cutting off everything up to the end of the frame, i.e. `input = input.Slice(payload.End)`

This means that when we return `true`, `payload` now contains the bytes that were sent to us, as a discontiguous buffer.

We should also take a look at `ParseFrameHeader`, which is a close cousin to `WriteFrameHeader`:

```c#
static int ParseFrameHeader(
    ReadOnlySpan<byte> input, out int messageId)
{
    var length = BinaryPrimitives
            .ReadInt32LittleEndian(input);
    messageId = BinaryPrimitives
            .ReadInt32LittleEndian(input.Slice(4));
    return length;
}
```

Once again, `BinaryPrimitives` is helping us out, and we are slicing the `input` in exactly the same way as before to get the two halves.

---

So; we can parse frames; now we need to act upon them; here's our client implementation:

```c#
protected override ValueTask OnReceiveAsync(
    ReadOnlySequence<byte> payload, int messageId)
{
    if (messageId != 0)
    {   // request/response
        TaskCompletionSource<IMemoryOwner<byte>> tcs;
        lock (_awaitingResponses)
        {
            if (_awaitingResponses.TryGetValue(messageId, out tcs))
            {
                _awaitingResponses.Remove(messageId);
            }
        }
        tcs?.TrySetResult(payload.Lease());
    }
    else
    {   // unsolicited
        MessageReceived?.Invoke(payload.Lease());
    }
    return default;
}
```

This code has two paths; it can be the request/response scenario, or it can be an out-of-band response message with no request. So; if we *have* a non-zero `messageId`, we check (synchronized) in our `_awaitingResponses` dictionary to see if we have a message awaiting completion. If we do, we use `TrySetResult` to complete the task (after exiting the `lock`), giving it a lease with the data from the message. Otherwise, we check whether the `MessageReceived` event is subscribed, and invoke that similarly. In both cases, the use of `?.` here means that we don't populate a leased array if nobody is listening. It will be the receiver's job to ensure the lease is disposed, as only they can know the lifetime.

## Service, please

We need to think a little about how we orchestrate this at the server. The `SimplPipeline` base type above relates to a *single* connection - it is essentially a proxy to a socket. But servers usually have many clients. Because of that, we'll create a server type that does the *actual processing*, that internally has a client-type that is our `SimplPipeline`, and a set of connected clients; so:

```c#
public abstract class SimplPipelineServer : IDisposable
{
    protected abstract ValueTask<IMemoryOwner<byte>> 
        OnReceiveForReplyAsync(IMemoryOwner<byte> message);
    
    public int ClientCount => _clients.Count;
    public Task RunClientAsync(IDuplexPipe pipe,
        CancellationToken cancellationToken = default)
        => new Client(pipe, this).RunAsync(cancellationToken);
    
    private class Client : SimplPipeline
    {
        public Task RunAsync(CancellationToken cancellationToken)
            => StartReceiveLoopAsync(cancellationToken);

        private readonly SimplPipelineServer _server;
        public Client(IDuplexPipe pipe, SimplPipelineServer server)
            : base(pipe) => _server = server;

        protected override async ValueTask OnReceiveAsync(
            ReadOnlySequence<byte> payload, int messageId)
        {
            using (var msg = payload.Lease())
            {
                var response = await _server.OnReceiveForReplyAsync(msg);
                await WriteAsync(response, messageId);
            }
        }
    }
}
```

So; our *publicly visible server type*, `SimplPipelineServer` has an `abstract` method for providing the implementation for *what we want to do with messages*: `OnReceiveForReplyAsync` - that takes a payload, and returns the response. Behind the scenes we have a set of clients, `_clients`, although the details of that aren't interesting.

We accept new clients via the `RunClientAsync` method; this might seem counter-intuitive, but the emerging architecture for pipelines servers (especially considering "kestrel" hosts) is to let an external host deal with listening and accepting connections, and all we need to do is have something that accepts an `IDuplexPipe` and returns a `Task`. In this case, what that *does* is create a new `Client` and start the client's read loop, `StartReceiveLoopAsync`. When the client receives a message (`OnReceiveAsync`), it asks the server for a response (`_server.OnReceiveForReplyAsync`), and then writes that response back via `WriteAsync`. Note that the version of `OnReceiveAsync` shown has the consequence of meaning that we can't handle multiple overlapped messages on the same connection at the same time; the "real" version has been aggressively uglified, to check whether `_server.OnReceiveForReplyAsync(msg)` has completed synchronously; if it hasn't, then it schedules a *continuation* to perform the `WriteAsync` (also handling the disposal of `msg`), and yields to the caller. It also optimizes for the "everything is synchronous" case.

The only other server API we need is a broadcast:

```c#
public async ValueTask<int> BroadcastAsync(
    ReadOnlyMemory<byte> message)
{
    int count = 0;
    foreach (var client in _clients)
    {
        try
        {
            await client.Key.SendAsync(message);
            count++;
        }
        catch { } // ignore failures on specific clients
    }
    return count;
}
```

(again, possibly with an overload that takes `IMemoryOwner<byte>`)

where `SendAsync` is simply:

```c#
public ValueTask SendAsync(ReadOnlyMemory<byte> message)
    => WriteAsync(message, 0);
```

## Putting it all together; implementing a client and server

So how can we *use* all of this? How can we get a working client and server? Let's start with the simpler of the two, the client:

```
using (var client = await SimplPipelineClient.ConnectAsync(
    new IPEndPoint(IPAddress.Loopback, 5000)))
{
    // subscribe to broadcasts
    client.MessageReceived += async msg => {
        if (!msg.Memory.IsEmpty)
            await WriteLineAsync('*', msg);
    };

    string line;
    while ((line = await Console.In.ReadLineAsync()) != null)
    {
        if (line == "q") break;

        using (var leased = line.Encode())
        {
            var response = await client.SendReciveAsync(leased.Memory);
            await WriteLineAsync('<', response);
        }     
    }
}
```

`SimplPipelineClient.ConnectAsync` here just uses `Pipelines.Sockets.Unofficial` to spin up a client socket pipeline, and starts the `StartReceiveLoopAsync()` method. Taking an additional dependency on `Pipelines.Sockets.Unofficial` is vexing, but right now there is no framework-supplied client-socket API for pipelines, so: it'll do the job.

This code sets up a simple console client that takes keyboard input; if it receives a `"q"` it quits; otherwise it sends the message to the server (`Encode`, not shown, is just a simple text-encode into a leased buffer), and writes the response. The `WriteLineAsync` method here takes a leased buffer, decodes it, and writes the output to the console - then disposes the buffer. We also listen for unsolicited messages via `MessageReceived`, and write those to the console with a different prefix.

The server is a little more involved; first we need to implement a server; in this case let's simply reverse the bytes we get:

```c#
class ReverseServer : SimplPipelineServer
{
    protected override ValueTask<IMemoryOwner<byte>>
        OnReceiveForReplyAsync(IMemoryOwner<byte> message)
    {
        // since the "message" outlives the response write,
        // we can do an in-place reverse and hand
        // the same buffer back
        var memory = message.Memory;
        Reverse(memory.Span); // details not shown
        return new ValueTask<IMemoryOwner<byte>>(memory);
    }
}
```

All this does is respond to messages by returning the same payload, but backwards. And yes, I realize that since we're dealing with text, this could go horribly wrong for grapheme-clusters and/or multi-byte code-points! I never said it was a *useful* server...

Next up, we need a host. Kestrel (the "ASP.NET Core" server) is an excellent choice there, but implementing a kestrel host requires introducing quite a few more concepts. But... since we already took a dependency on `Pipelines.Sockets.Unofficial` for the client, we can use that for ther server host with a few lines of code:

```c#
class SimplPipelineSocketServer : SocketServer
{
    public SimplPipelineServer Server { get; }

    public SimplPipelineSocketServer(SimplPipelineServer server)
        => Server = server;

    protected override Task OnClientConnectedAsync(
        in ClientConnection client)
        => Server.RunClientAsync(client.Transport);

    public static SimplPipelineSocketServer For<T>()
        where T : SimplPipelineServer, new()
        => new SimplPipelineSocketServer(new T());

    protected override void Dispose(bool disposing)
    {
        if (disposing) Server.Dispose();
    }
}
```

The key line in here is our `OnClientConnectedAsync` method, which is how we accept new connections, simply by passing down the `client.Transport` (an `IDuplexPipe`). Hosting in Kestrel works very similarly, except you subclass `ConnectionHandler` instead of `SocketServer`, and `override` the `OnConnectedAsync` method - but there are a few more steps involved in plumbing everything together. Kestrel, however, has advantages such as supporting exotic socket APIs.

So, let's whack together a console that interacts with the server:

```c#
using (var socket =
    SimplPipelineSocketServer.For<ReverseServer>())
{
    socket.Listen(new IPEndPoint(IPAddress.Loopback, 5000));
    
    string line;
    while ((line = await Console.In.ReadLineAsync()) != null)
    {
        if (line == "q") break;

        int clientCount, len;
        using (var leased = line.Encode())
        {
            len = leased.Memory.Length;
            clientCount = await socket.Server.BroadcastAsync(leased.Memory);
        }
        await Console.Out.WriteLineAsync(
            $"Broadcast {len} bytes to {clientCount} clients");
    }
}
```

This works much like the client, except any input other than `"q"` is *broadcast* to all the clients.

## Now race your horses

TODO: Marc to add benchmark data, when I can get `SimplSockets` to work... it currently works fine in the RERPL but blows up during the stress test; `SimplPipelines` is fine :)

TODO: Marc to `merge pipelines` (into master) on my github fork, and update the link below

## Summary

This has been a long read, but I hope I've conveyed some useful practical advice and tips for working with pipelines in real systems, in a way that is directly translatable to your *own* requirements. If you want to play with the code in more depth, or see it in action, you can [see my fork here](https://github.com/mgravell/simplsockets/tree/pipelines/) **TODO update link**. Note: 

