using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;

namespace UDriver
{
    /// <summary>A copy started with -udriver-slot N keeps persistentDataPath and PlayerPrefs in its own folder, so copies of one game do not share saves.
    /// Works by redirecting the engine's managed methods inside this process (Mono); IL2CPP builds keep the shared folder.</summary>
    public static class Slots
    {
        public static int Slot;
        public static string Folder = "", Problem;

        static string _prefsFile;
        static readonly Dictionary<string, string> Prefs = new Dictionary<string, string>();   // key -> "i|f|s" + value

        public static void Redirect(int slot)
        {
            Slot = slot;
            try
            {
                Folder = Path.Combine(Application.persistentDataPath, "udriver-slot-" + slot);
                Directory.CreateDirectory(Folder);
                _prefsFile = Path.Combine(Folder, "PlayerPrefs.txt");
                if (File.Exists(_prefsFile))
                    foreach (var line in File.ReadAllLines(_prefsFile))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab > 0) Prefs[Uri.UnescapeDataString(line.Substring(0, tab))] = Uri.UnescapeDataString(line.Substring(tab + 1));
                    }
                if (Type.GetType("Mono.Runtime") == null) { Problem = "this build is not Mono"; return; }
                var app = typeof(Application).GetProperty("persistentDataPath").GetGetMethod();
                Patch(app, typeof(Slots).GetMethod("PersistentDataPath", BindingFlags.NonPublic | BindingFlags.Static));
                foreach (var m in typeof(PlayerPrefs).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.Name.EndsWith("_Injected", StringComparison.Ordinal)) continue;
                    var to = typeof(Slots).GetMethod("Prefs" + m.Name, BindingFlags.NonPublic | BindingFlags.Static, null, m.GetParameters().Select(p => p.ParameterType).ToArray(), null);
                    if (to != null && to.ReturnType == m.ReturnType) Patch(m, to);
                }
            }
            catch (Exception e) { Problem = e.GetType().Name + ": " + e.Message; }
        }

        static string PersistentDataPath() { return Folder; }

        static void Put(string key, string value)
        {
            lock (Prefs)
            {
                if (key == null) throw new ArgumentNullException("key");
                if (value == null) Prefs.Remove(key); else Prefs[key] = value;
                File.WriteAllLines(_prefsFile, Prefs.Select(p => Uri.EscapeDataString(p.Key) + "\t" + Uri.EscapeDataString(p.Value)).ToArray());
            }
        }

        static string Get(string key, char kind)
        {
            string v;
            lock (Prefs) return key != null && Prefs.TryGetValue(key, out v) && v.Length > 0 && v[0] == kind ? v.Substring(1) : null;
        }

        static bool PrefsTrySetInt(string key, int value) { Put(key, "i" + value.ToString(CultureInfo.InvariantCulture)); return true; }
        static bool PrefsTrySetFloat(string key, float value) { Put(key, "f" + value.ToString("R", CultureInfo.InvariantCulture)); return true; }
        static bool PrefsTrySetSetString(string key, string value) { Put(key, "s" + (value ?? "")); return true; }
        static void PrefsSetInt(string key, int value) { PrefsTrySetInt(key, value); }
        static void PrefsSetFloat(string key, float value) { PrefsTrySetFloat(key, value); }
        static void PrefsSetString(string key, string value) { PrefsTrySetSetString(key, value); }
        static int PrefsGetInt(string key, int defaultValue) { int v; return int.TryParse(Get(key, 'i'), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : defaultValue; }
        static int PrefsGetInt(string key) { return PrefsGetInt(key, 0); }
        static float PrefsGetFloat(string key, float defaultValue) { float v; return float.TryParse(Get(key, 'f'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : defaultValue; }
        static float PrefsGetFloat(string key) { return PrefsGetFloat(key, 0f); }
        static string PrefsGetString(string key, string defaultValue) { return Get(key, 's') ?? defaultValue; }
        static string PrefsGetString(string key) { return PrefsGetString(key, ""); }
        static bool PrefsHasKey(string key) { lock (Prefs) return key != null && Prefs.ContainsKey(key); }
        static void PrefsDeleteKey(string key) { Put(key, null); }
        static void PrefsDeleteAll() { lock (Prefs) { Prefs.Clear(); File.WriteAllText(_prefsFile, ""); } }
        static void PrefsSave() { }

        // ---------------- jumps written over the start of a JIT-compiled method ----------------

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void CopyFn(IntPtr dst, IntPtr src, long len, IntPtr toggle);
        static CopyFn _copy;
        static IntPtr _toggle;

        const string Sys = "/usr/lib/libSystem.B.dylib";
        [DllImport(Sys, EntryPoint = "mmap")] static extern IntPtr MacMmap(IntPtr addr, UIntPtr len, int prot, int flags, int fd, IntPtr off);
        [DllImport(Sys, EntryPoint = "mprotect")] static extern int MacMprotect(IntPtr addr, UIntPtr len, int prot);
        [DllImport(Sys)] static extern IntPtr dlopen(string path, int mode);
        [DllImport(Sys)] static extern IntPtr dlsym(IntPtr handle, string name);
        [DllImport(Sys)] static extern void sys_icache_invalidate(IntPtr start, UIntPtr len);
        [DllImport("libc", EntryPoint = "mprotect")] static extern int LinuxMprotect(IntPtr addr, UIntPtr len, int prot);
        [DllImport("kernel32")] static extern bool VirtualProtect(IntPtr addr, UIntPtr size, uint prot, out uint old);

        static void Patch(MethodBase from, MethodInfo to)
        {
            RuntimeHelpers.PrepareMethod(from.MethodHandle);
            RuntimeHelpers.PrepareMethod(to.MethodHandle);
            IntPtr src = from.MethodHandle.GetFunctionPointer(), dst = to.MethodHandle.GetFunctionPointer();
            bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            byte[] jump;
            if (arm) { jump = new byte[16]; BitConverter.GetBytes(0x58000050u).CopyTo(jump, 0); BitConverter.GetBytes(0xD61F0200u).CopyTo(jump, 4); BitConverter.GetBytes(dst.ToInt64()).CopyTo(jump, 8); }
            else if (RuntimeInformation.ProcessArchitecture == Architecture.X64) { jump = new byte[12]; jump[0] = 0x48; jump[1] = 0xB8; BitConverter.GetBytes(dst.ToInt64()).CopyTo(jump, 2); jump[10] = 0xFF; jump[11] = 0xE0; }
            else throw new PlatformNotSupportedException("slot folders need an x64 or Apple arm64 Mono player");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && arm)
            {
                // Mono's code pages are MAP_JIT: writable only while this thread cannot execute them, so the copy runs from a small page of its own.
                if (_copy == null)
                {
                    uint[] code =
                    {
                        0xa9bd7bfd, 0x910003fd, 0xa90153f3, 0xa9025bf5, 0xaa0003f3, 0xaa0103f4, 0xaa0203f5, 0xaa0303f6, 0x52800000, 0xd63f02c0,
                        0x38401689, 0x38001669, 0xf10006b5, 0x54ffffa1, 0x52800020, 0xd63f02c0, 0xa9425bf5, 0xa94153f3, 0xa8c37bfd, 0xd65f03c0,
                    };
                    IntPtr page = MacMmap(IntPtr.Zero, (UIntPtr)16384, 3, 0x1002, -1, IntPtr.Zero);
                    if (page == new IntPtr(-1)) throw new InvalidOperationException("mmap failed");
                    for (int i = 0; i < code.Length; i++) Marshal.WriteInt32(page, i * 4, (int)code[i]);
                    if (MacMprotect(page, (UIntPtr)16384, 5) != 0) throw new InvalidOperationException("mprotect failed");
                    sys_icache_invalidate(page, (UIntPtr)16384);
                    _toggle = dlsym(dlopen(Sys, 1), "pthread_jit_write_protect_np");
                    if (_toggle == IntPtr.Zero) throw new InvalidOperationException("no pthread_jit_write_protect_np");
                    _copy = (CopyFn)Marshal.GetDelegateForFunctionPointer(page, typeof(CopyFn));
                }
                var buf = Marshal.AllocHGlobal(jump.Length);
                Marshal.Copy(jump, 0, buf, jump.Length);
                _copy(src, buf, jump.Length, _toggle);
                Marshal.FreeHGlobal(buf);
                sys_icache_invalidate(src, (UIntPtr)jump.Length);
                return;
            }
            long start = src.ToInt64() & ~0xFFFL;
            var span = (UIntPtr)(src.ToInt64() + jump.Length - start);
            uint old;
            bool ok = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? VirtualProtect(new IntPtr(start), span, 0x40, out old)
                : (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? MacMprotect(new IntPtr(start), span, 7) : LinuxMprotect(new IntPtr(start), span, 7)) == 0;
            if (!ok) throw new InvalidOperationException("cannot make the code writable");
            Marshal.Copy(jump, 0, src, jump.Length);
        }
    }
}
