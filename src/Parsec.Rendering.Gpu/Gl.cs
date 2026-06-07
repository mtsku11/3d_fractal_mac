using System.Runtime.InteropServices;
using System.Text;

namespace Parsec.Rendering.Gpu;

/// <summary>
/// A minimal OpenGL 4.3 wrapper covering exactly the calls Parsec's compute
/// pipeline needs, loaded via a GetProcAddress-style function-pointer loader.
/// </summary>
/// <remarks>
/// <para>
/// This is the single GL-calling idiom shared by both the headless CLI path
/// (where the proc loader comes from the offscreen context) and the Avalonia
/// app (where it comes from <c>GlInterface.GetProcAddress</c>). No binding
/// library's static GL class is used anywhere; every entrypoint is a delegate
/// loaded by name and invoked. This matches the approach used in the sibling
/// Helios codebase, so the two projects share one mental model.
/// </para>
/// <para>
/// Construct once, on the thread/context where GL is current, and use only
/// while that context remains current. In Avalonia that means constructing in
/// <c>OnOpenGlInit</c> and using only inside the OnOpenGl* callbacks.
/// </para>
/// </remarks>
public sealed class Gl
{
    // ---- delegate types (C ABI signatures) ----
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint D_CreateShader(uint type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_ShaderSource(uint shader, int count, string[] str, int[]? length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_CompileShader(uint shader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_GetShaderiv(uint shader, uint pname, out int param);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_GetShaderInfoLog(uint shader, int maxLength, out int length, byte[] infoLog);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint D_CreateProgram();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_AttachShader(uint program, uint shader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_LinkProgram(uint program);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_GetProgramiv(uint program, uint pname, out int param);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_GetProgramInfoLog(uint program, int maxLength, out int length, byte[] infoLog);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_DetachShader(uint program, uint shader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_DeleteShader(uint shader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_DeleteProgram(uint program);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_UseProgram(uint program);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int  D_GetUniformLocation(uint program, string name);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_GenBuffers(int n, [Out] uint[] buffers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_BindBuffer(uint target, uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_BufferData(uint target, IntPtr size, IntPtr data, uint usage);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_BindBufferBase(uint target, uint index, uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_GetBufferSubData(uint target, IntPtr offset, IntPtr size, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_DeleteBuffers(int n, uint[] buffers);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_DispatchCompute(uint x, uint y, uint z);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_MemoryBarrier(uint barriers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_Finish();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr D_GetString(uint name);

    // ---- blit / graphics path ----
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_GenTextures(int n, [Out] uint[] textures);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_BindTexture(uint target, uint texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_TexImage2D(uint target, int level, int internalFormat, int width, int height, int border, uint format, uint type, IntPtr pixels);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_TexParameteri(uint target, uint pname, int param);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_ActiveTexture(uint texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_DeleteTextures(int n, uint[] textures);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_GenVertexArrays(int n, [Out] uint[] arrays);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_BindVertexArray(uint array);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_DeleteVertexArrays(int n, uint[] arrays);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_BindFramebuffer(uint target, uint framebuffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_Viewport(int x, int y, int width, int height);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_ClearColor(float r, float g, float b, float a);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_Clear(uint mask);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_DrawArrays(uint mode, int first, int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_Uniform1i(int location, int v0);

    // ---- loaded delegates ----
    private readonly D_CreateShader _createShader;
    private readonly D_ShaderSource _shaderSource;
    private readonly D_CompileShader _compileShader;
    private readonly D_GetShaderiv _getShaderiv;
    private readonly D_GetShaderInfoLog _getShaderInfoLog;
    private readonly D_CreateProgram _createProgram;
    private readonly D_AttachShader _attachShader;
    private readonly D_LinkProgram _linkProgram;
    private readonly D_GetProgramiv _getProgramiv;
    private readonly D_GetProgramInfoLog _getProgramInfoLog;
    private readonly D_DetachShader _detachShader;
    private readonly D_DeleteShader _deleteShader;
    private readonly D_DeleteProgram _deleteProgram;
    private readonly D_UseProgram _useProgram;
    private readonly D_GetUniformLocation _getUniformLocation;
    private readonly D_GenBuffers _genBuffers;
    private readonly D_BindBuffer _bindBuffer;
    private readonly D_BufferData _bufferData;
    private readonly D_BindBufferBase _bindBufferBase;
    private readonly D_GetBufferSubData _getBufferSubData;
    private readonly D_DeleteBuffers _deleteBuffers;
    private readonly D_DispatchCompute? _dispatchCompute;
    private readonly D_MemoryBarrier? _memoryBarrier;
    private readonly D_Finish _finish;
    private readonly D_GetString _getString;
    private readonly D_GenTextures _genTextures;
    private readonly D_BindTexture _bindTexture;
    private readonly D_TexImage2D _texImage2D;
    private readonly D_TexParameteri _texParameteri;
    private readonly D_ActiveTexture _activeTexture;
    private readonly D_DeleteTextures _deleteTextures;
    private readonly D_GenVertexArrays _genVertexArrays;
    private readonly D_BindVertexArray _bindVertexArray;
    private readonly D_DeleteVertexArrays _deleteVertexArrays;
    private readonly D_BindFramebuffer _bindFramebuffer;
    private readonly D_Viewport _viewport;
    private readonly D_ClearColor _clearColor;
    private readonly D_Clear _clear;
    private readonly D_DrawArrays _drawArrays;
    private readonly D_Uniform1i _uniform1i;

    /// <summary>
    /// Build the GL wrapper from a proc-address loader. The loader must return
    /// a valid function pointer for a given GL entrypoint name, or
    /// <see cref="IntPtr.Zero"/> if unavailable.
    /// </summary>
    public Gl(Func<string, IntPtr> getProcAddress)
    {
        T Load<T>(string name) where T : Delegate
        {
            var ptr = getProcAddress(name);
            if (ptr == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"GL entrypoint '{name}' could not be loaded. The current context may not " +
                    $"support OpenGL 4.3 core (compute shaders / SSBOs).");
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }
        T? TryLoad<T>(string name) where T : Delegate
        {
            var ptr = getProcAddress(name);
            return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        _createShader = Load<D_CreateShader>("glCreateShader");
        _shaderSource = Load<D_ShaderSource>("glShaderSource");
        _compileShader = Load<D_CompileShader>("glCompileShader");
        _getShaderiv = Load<D_GetShaderiv>("glGetShaderiv");
        _getShaderInfoLog = Load<D_GetShaderInfoLog>("glGetShaderInfoLog");
        _createProgram = Load<D_CreateProgram>("glCreateProgram");
        _attachShader = Load<D_AttachShader>("glAttachShader");
        _linkProgram = Load<D_LinkProgram>("glLinkProgram");
        _getProgramiv = Load<D_GetProgramiv>("glGetProgramiv");
        _getProgramInfoLog = Load<D_GetProgramInfoLog>("glGetProgramInfoLog");
        _detachShader = Load<D_DetachShader>("glDetachShader");
        _deleteShader = Load<D_DeleteShader>("glDeleteShader");
        _deleteProgram = Load<D_DeleteProgram>("glDeleteProgram");
        _useProgram = Load<D_UseProgram>("glUseProgram");
        _getUniformLocation = Load<D_GetUniformLocation>("glGetUniformLocation");
        _genBuffers = Load<D_GenBuffers>("glGenBuffers");
        _bindBuffer = Load<D_BindBuffer>("glBindBuffer");
        _bufferData = Load<D_BufferData>("glBufferData");
        _bindBufferBase = Load<D_BindBufferBase>("glBindBufferBase");
        _getBufferSubData = Load<D_GetBufferSubData>("glGetBufferSubData");
        _deleteBuffers = Load<D_DeleteBuffers>("glDeleteBuffers");
        _dispatchCompute = TryLoad<D_DispatchCompute>("glDispatchCompute");
        _memoryBarrier = TryLoad<D_MemoryBarrier>("glMemoryBarrier");
        _finish = Load<D_Finish>("glFinish");
        _getString = Load<D_GetString>("glGetString");

        _genTextures = Load<D_GenTextures>("glGenTextures");
        _bindTexture = Load<D_BindTexture>("glBindTexture");
        _texImage2D = Load<D_TexImage2D>("glTexImage2D");
        _texParameteri = Load<D_TexParameteri>("glTexParameteri");
        _activeTexture = Load<D_ActiveTexture>("glActiveTexture");
        _deleteTextures = Load<D_DeleteTextures>("glDeleteTextures");
        _genVertexArrays = Load<D_GenVertexArrays>("glGenVertexArrays");
        _bindVertexArray = Load<D_BindVertexArray>("glBindVertexArray");
        _deleteVertexArrays = Load<D_DeleteVertexArrays>("glDeleteVertexArrays");
        _bindFramebuffer = Load<D_BindFramebuffer>("glBindFramebuffer");
        _viewport = Load<D_Viewport>("glViewport");
        _clearColor = Load<D_ClearColor>("glClearColor");
        _clear = Load<D_Clear>("glClear");
        _drawArrays = Load<D_DrawArrays>("glDrawArrays");
        _uniform1i = Load<D_Uniform1i>("glUniform1i");
    }

    // ---- shader / program ----
    public uint CreateShader(uint type) => _createShader(type);
    public void ShaderSource(uint shader, string source) => _shaderSource(shader, 1, new[] { source }, null);
    public void CompileShader(uint shader) => _compileShader(shader);
    public int GetShaderInt(uint shader, uint pname) { _getShaderiv(shader, pname, out int v); return v; }
    public uint CreateProgram() => _createProgram();
    public void AttachShader(uint program, uint shader) => _attachShader(program, shader);
    public void LinkProgram(uint program) => _linkProgram(program);
    public int GetProgramInt(uint program, uint pname) { _getProgramiv(program, pname, out int v); return v; }
    public void DetachShader(uint program, uint shader) => _detachShader(program, shader);
    public void DeleteShader(uint shader) => _deleteShader(shader);
    public void DeleteProgram(uint program) => _deleteProgram(program);
    public void UseProgram(uint program) => _useProgram(program);
    public int GetUniformLocation(uint program, string name) => _getUniformLocation(program, name);

    public string GetShaderInfoLog(uint shader)
    {
        int len = GetShaderInt(shader, GlConst.InfoLogLength);
        if (len <= 0) return string.Empty;
        var buf = new byte[len];
        _getShaderInfoLog(shader, len, out int written, buf);
        return Encoding.UTF8.GetString(buf, 0, Math.Max(0, written));
    }

    public string GetProgramInfoLog(uint program)
    {
        int len = GetProgramInt(program, GlConst.InfoLogLength);
        if (len <= 0) return string.Empty;
        var buf = new byte[len];
        _getProgramInfoLog(program, len, out int written, buf);
        return Encoding.UTF8.GetString(buf, 0, Math.Max(0, written));
    }

    // ---- buffers ----
    public uint GenBuffer() { var a = new uint[1]; _genBuffers(1, a); return a[0]; }
    public void BindBuffer(uint target, uint buffer) => _bindBuffer(target, buffer);
    public void BufferData(uint target, IntPtr size, IntPtr data, uint usage) => _bufferData(target, size, data, usage);
    public void BindBufferBase(uint target, uint index, uint buffer) => _bindBufferBase(target, index, buffer);
    public void GetBufferSubData(uint target, IntPtr offset, IntPtr size, IntPtr data) => _getBufferSubData(target, offset, size, data);
    public void DeleteBuffer(uint buffer) => _deleteBuffers(1, new[] { buffer });

    // ---- compute / sync / query ----
    public bool SupportsCompute => _dispatchCompute != null;
    public void DispatchCompute(uint x, uint y, uint z)
    {
        if (_dispatchCompute == null) throw new NotSupportedException("glDispatchCompute not available (context < 4.3)");
        _dispatchCompute(x, y, z);
    }
    public void MemoryBarrier(uint barriers)
    {
        if (_memoryBarrier == null) throw new NotSupportedException("glMemoryBarrier not available (context < 4.2)");
        _memoryBarrier(barriers);
    }
    public void Finish() => _finish();

    public string GetString(uint name)
    {
        var ptr = _getString(name);
        return ptr == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringAnsi(ptr) ?? string.Empty);
    }

    // ---- blit / graphics path ----
    public uint GenTexture() { var a = new uint[1]; _genTextures(1, a); return a[0]; }
    public void BindTexture(uint target, uint texture) => _bindTexture(target, texture);
    public void TexImage2D(uint target, int level, int internalFormat, int width, int height, int border, uint format, uint type, IntPtr pixels)
        => _texImage2D(target, level, internalFormat, width, height, border, format, type, pixels);
    public void TexParameteri(uint target, uint pname, int param) => _texParameteri(target, pname, param);
    public void ActiveTexture(uint texture) => _activeTexture(texture);
    public void DeleteTexture(uint texture) => _deleteTextures(1, new[] { texture });
    public uint GenVertexArray() { var a = new uint[1]; _genVertexArrays(1, a); return a[0]; }
    public void BindVertexArray(uint array) => _bindVertexArray(array);
    public void DeleteVertexArray(uint array) => _deleteVertexArrays(1, new[] { array });
    public void BindFramebuffer(uint target, uint framebuffer) => _bindFramebuffer(target, framebuffer);
    public void Viewport(int x, int y, int width, int height) => _viewport(x, y, width, height);
    public void ClearColor(float r, float g, float b, float a) => _clearColor(r, g, b, a);
    public void Clear(uint mask) => _clear(mask);
    public void DrawArrays(uint mode, int first, int count) => _drawArrays(mode, first, count);
    public void Uniform1i(int location, int v0) => _uniform1i(location, v0);

    /// <summary>
    /// Compile and link a vertex+fragment program (for the on-screen blit).
    /// Returns the program handle. Throws on compile/link failure.
    /// </summary>
    public uint CreateGraphicsProgram(string vertexSrc, string fragmentSrc)
    {
        uint vs = CompileOne(GlConst.VertexShader, vertexSrc, "vertex");
        uint fs = CompileOne(GlConst.FragmentShader, fragmentSrc, "fragment");
        uint program = CreateProgram();
        AttachShader(program, vs);
        AttachShader(program, fs);
        LinkProgram(program);
        DetachShader(program, vs);
        DetachShader(program, fs);
        DeleteShader(vs);
        DeleteShader(fs);
        if (GetProgramInt(program, GlConst.LinkStatus) == 0)
        {
            string log = GetProgramInfoLog(program);
            DeleteProgram(program);
            throw new InvalidOperationException($"Blit program failed to link:\n{log}");
        }
        return program;

        uint CompileOne(uint type, string src, string label)
        {
            uint sh = CreateShader(type);
            ShaderSource(sh, src);
            CompileShader(sh);
            if (GetShaderInt(sh, GlConst.CompileStatus) == 0)
            {
                string log = GetShaderInfoLog(sh);
                DeleteShader(sh);
                throw new InvalidOperationException($"Blit {label} shader failed to compile:\n{log}");
            }
            return sh;
        }
    }
}
