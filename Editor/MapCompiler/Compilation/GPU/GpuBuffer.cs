using Silk.NET.OpenGL;
using System;
using System.Runtime.InteropServices;

namespace MapCompiler.Compilation.GPU;
public sealed class GpuBuffer : IDisposable
{
    private readonly GL gl;
    public uint Handle { get; private set; }

    public GpuBuffer(GL gl)
    {
        this.gl = gl;
        Handle = gl.GenBuffer();
    }

    public unsafe void Upload<T>(ReadOnlySpan<T> data, BufferUsageARB usage = BufferUsageARB.StaticDraw) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(data);
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, Handle);
        fixed (byte* ptr = bytes)
        {
            gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)bytes.Length, ptr, usage);
        }

        var error = gl.GetError();
        if (error != GLEnum.NoError)
        {
            //throw new InvalidOperationException($"BufferData failed allocating {bytes.Length:N0} bytes (GL error: {error}).");
            Console.WriteLine($"WARNING: BufferData failed allocating {bytes.Length:N0} bytes (GL error: {error}).");
        }
    }

    public void BindRange(uint slot, nint offsetBytes, nuint sizeBytes)
    {
        gl.BindBufferRange(BufferTargetARB.ShaderStorageBuffer, slot, Handle, offsetBytes, sizeBytes);
    }

    public void BindBase(uint slot)
    {
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, slot, Handle);
    }

    public unsafe T[] ReadBack<T>(int count) where T : unmanaged
    {
        gl.MemoryBarrier(MemoryBarrierMask.BufferUpdateBarrierBit);

        var result = new T[count];
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, Handle);

        void* mapped = gl.MapBufferRange(BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(count * sizeof(T)), (uint)GLEnum.MapReadBit);

        if (mapped == null)
        {
            var error = gl.GetError();
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
            //throw new InvalidOperationException($"MapBufferRange returned null (GL error: {error}).");
            Console.WriteLine($"MapBufferRange returned null (GL error: {error}).");
        }

        new Span<byte>(mapped, count * sizeof(T)).CopyTo(MemoryMarshal.AsBytes(result.AsSpan()));
        gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);

        return result;
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            gl.DeleteBuffer(Handle);
            Handle = 0;
        }
    }
}