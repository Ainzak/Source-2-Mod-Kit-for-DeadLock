using System.Diagnostics;
using System.Runtime.InteropServices;
using S2ModKit.Domain;

namespace S2ModKit.Adapters.Source2;

internal sealed class NativeMeshOptimizerCodec : IMeshOptimizerCodec
{
    private const int MaximumDecodedBufferBytes = 512 * 1024 * 1024;
    private const long MaximumNativeLibraryBytes = 128L * 1024 * 1024;
    private const int SupportedVertexEncodingVersion = 1;
    private static readonly object EncodeGate = new();

    private readonly DecodeBufferDelegate decodeVertexBuffer;
    private readonly DecodeBufferDelegate decodeIndexBuffer;
    private readonly EncodeVertexBufferBoundDelegate encodeVertexBufferBound;
    private readonly EncodeVertexBufferDelegate encodeVertexBuffer;
    private readonly EncodeVertexVersionDelegate encodeVertexVersion;
    private nint libraryHandle;

    private NativeMeshOptimizerCodec(nint handle, ContentHash binaryHash, string version)
    {
        libraryHandle = handle;
        try
        {
            decodeVertexBuffer = LoadExport<DecodeBufferDelegate>(handle, "meshopt_decodeVertexBuffer");
            decodeIndexBuffer = LoadExport<DecodeBufferDelegate>(handle, "meshopt_decodeIndexBuffer");
            encodeVertexBufferBound = LoadExport<EncodeVertexBufferBoundDelegate>(handle, "meshopt_encodeVertexBufferBound");
            encodeVertexBuffer = LoadExport<EncodeVertexBufferDelegate>(handle, "meshopt_encodeVertexBuffer");
            encodeVertexVersion = LoadExport<EncodeVertexVersionDelegate>(handle, "meshopt_encodeVertexVersion");
            Identity = new GeometryCodecIdentity(
                "meshoptimizer",
                "source2-vertex-v1",
                RuntimeInformation.RuntimeIdentifier,
                binaryHash,
                version);
        }
        catch
        {
            NativeLibrary.Free(handle);
            libraryHandle = 0;
            throw;
        }
    }

    public GeometryCodecIdentity Identity { get; }

    public static NativeMeshOptimizerCodec Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The configured meshoptimizer library does not exist.", fullPath);
        }

        if (file.Length is < 1 or > MaximumNativeLibraryBytes)
        {
            throw new InvalidDataException($"The configured meshoptimizer library size {file.Length} is outside the supported range.");
        }

        ContentHash binaryHash;
        using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            binaryHash = ContentHash.Compute(stream);
        }

        var fileVersion = FileVersionInfo.GetVersionInfo(fullPath);
        var version = fileVersion.FileVersion ?? fileVersion.ProductVersion ?? "unversioned";
        var handle = NativeLibrary.Load(fullPath);
        return new NativeMeshOptimizerCodec(handle, binaryHash, version);
    }

    public byte[] DecodeVertexBuffer(byte[] encoded, int vertexCount, int vertexStride)
    {
        ThrowIfDisposed();
        ValidateEncodedBuffer(encoded);
        var decodedLength = ValidateVertexShape(vertexCount, vertexStride);
        var decoded = new byte[decodedLength];
        var result = InvokeDecode(decodeVertexBuffer, decoded, encoded, vertexCount, vertexStride);
        if (result != 0)
        {
            throw new InvalidDataException($"meshopt_decodeVertexBuffer failed with result {result}.");
        }

        return decoded;
    }

    public byte[] DecodeIndexBuffer(byte[] encoded, int indexCount, int indexStride)
    {
        ThrowIfDisposed();
        ValidateEncodedBuffer(encoded);
        if (indexStride is not (2 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(indexStride), indexStride, "Index stride must be 2 or 4 bytes.");
        }

        var decodedLength = ValidateDecodedLength(indexCount, indexStride, nameof(indexCount));
        var decoded = new byte[decodedLength];
        var result = InvokeDecode(decodeIndexBuffer, decoded, encoded, indexCount, indexStride);
        if (result != 0)
        {
            throw new InvalidDataException($"meshopt_decodeIndexBuffer failed with result {result}.");
        }

        return decoded;
    }

    public byte[] EncodeVertexBuffer(byte[] decoded, int vertexCount, int vertexStride, int version)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(decoded);
        var expectedLength = ValidateVertexShape(vertexCount, vertexStride);
        if (decoded.Length != expectedLength)
        {
            throw new ArgumentException($"Decoded vertex buffer length {decoded.Length} does not match expected length {expectedLength}.", nameof(decoded));
        }

        if (version != SupportedVertexEncodingVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, $"Only meshoptimizer vertex encoding version {SupportedVertexEncodingVersion} is supported.");
        }

        lock (EncodeGate)
        {
            encodeVertexVersion(version);
            var bound = encodeVertexBufferBound(checked((nuint)vertexCount), checked((nuint)vertexStride));
            if (bound == 0 || bound > MaximumDecodedBufferBytes)
            {
                throw new InvalidDataException($"meshopt_encodeVertexBufferBound returned unsupported size {bound}.");
            }

            var encoded = new byte[checked((int)bound)];
            GCHandle encodedHandle = default;
            GCHandle decodedHandle = default;
            try
            {
                encodedHandle = GCHandle.Alloc(encoded, GCHandleType.Pinned);
                decodedHandle = GCHandle.Alloc(decoded, GCHandleType.Pinned);
                var size = encodeVertexBuffer(
                    encodedHandle.AddrOfPinnedObject(),
                    bound,
                    decodedHandle.AddrOfPinnedObject(),
                    checked((nuint)vertexCount),
                    checked((nuint)vertexStride));
                if (size == 0 || size > bound)
                {
                    throw new InvalidDataException($"meshopt_encodeVertexBuffer returned invalid size {size} for bound {bound}.");
                }

                Array.Resize(ref encoded, checked((int)size));
                return encoded;
            }
            finally
            {
                if (decodedHandle.IsAllocated)
                {
                    decodedHandle.Free();
                }

                if (encodedHandle.IsAllocated)
                {
                    encodedHandle.Free();
                }
            }
        }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref libraryHandle, 0);
        if (handle != 0)
        {
            NativeLibrary.Free(handle);
        }
    }

    private static T LoadExport<T>(nint handle, string name)
        where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));

    private static int InvokeDecode(
        DecodeBufferDelegate decode,
        byte[] decoded,
        byte[] encoded,
        int elementCount,
        int elementSize)
    {
        GCHandle decodedHandle = default;
        GCHandle encodedHandle = default;
        try
        {
            decodedHandle = GCHandle.Alloc(decoded, GCHandleType.Pinned);
            encodedHandle = GCHandle.Alloc(encoded, GCHandleType.Pinned);
            return decode(
                decodedHandle.AddrOfPinnedObject(),
                checked((nuint)elementCount),
                checked((nuint)elementSize),
                encodedHandle.AddrOfPinnedObject(),
                checked((nuint)encoded.Length));
        }
        finally
        {
            if (encodedHandle.IsAllocated)
            {
                encodedHandle.Free();
            }

            if (decodedHandle.IsAllocated)
            {
                decodedHandle.Free();
            }
        }
    }

    private static int ValidateVertexShape(int vertexCount, int vertexStride)
    {
        if (vertexStride is < 4 or > 256 || vertexStride % 4 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(vertexStride), vertexStride, "Vertex stride must be a multiple of 4 in the range [4, 256].");
        }

        return ValidateDecodedLength(vertexCount, vertexStride, nameof(vertexCount));
    }

    private static int ValidateDecodedLength(int elementCount, int elementSize, string parameterName)
    {
        if (elementCount < 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, elementCount, "Element count must be positive.");
        }

        var length = checked((long)elementCount * elementSize);
        if (length > MaximumDecodedBufferBytes)
        {
            throw new ArgumentOutOfRangeException(parameterName, elementCount, $"Decoded buffers may not exceed {MaximumDecodedBufferBytes} bytes.");
        }

        return checked((int)length);
    }

    private static void ValidateEncodedBuffer(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length == 0)
        {
            throw new ArgumentException("Encoded buffer must not be empty.", nameof(encoded));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(libraryHandle == 0, this);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DecodeBufferDelegate(
        nint destination,
        nuint elementCount,
        nuint elementSize,
        nint buffer,
        nuint bufferSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint EncodeVertexBufferBoundDelegate(nuint vertexCount, nuint vertexSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint EncodeVertexBufferDelegate(
        nint buffer,
        nuint bufferSize,
        nint vertices,
        nuint vertexCount,
        nuint vertexSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EncodeVertexVersionDelegate(int version);
}
