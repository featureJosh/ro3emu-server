// Minimal xLua/Unity boundary for exercising the production hook without proprietary client assemblies.
namespace Obf_R
{
    public sealed class Obf_ff
    {
        private readonly Dictionary<object, object> values = new();
        public T Obf_RuA<T>(string key) => Obf_ruA<string, T>(key);
        public T Obf_ruA<K, T>(K key) => values.TryGetValue(key, out var value) ? (T)value : default;
        public void Obf_luA<K, T>(K key, T value) => values[key] = value;
    }

    public sealed class Obf_Ef(Func<object[], object[]> call)
    {
        public object[] Obf_HuA(params object[] args) => call(args);
    }
}

namespace UnityEngine
{
    public static class Debug
    {
        public static void Log(string message) { }
        public static void LogWarning(string message) { }
    }
}

namespace Ro3Emu.ClientHooks
{
    internal static class LuaPatches
    {
        internal static long ToLong(object value) => value switch
        {
            long l => l, int i => i, double d => (long)d, float f => (long)f, _ => long.MinValue,
        };
    }
}
