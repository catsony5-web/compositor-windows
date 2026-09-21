using Compositor.Windows;
public static class Harness {
[STAThread] public static int Main() { int failed = 0, passed = 0; AdvancedToolTests.Run((name,test) => { try { test(); Console.WriteLine("PASS " + name); passed++; } catch(Exception e) { Console.WriteLine("FAIL " + name + ": " + e); failed++; } }); Console.WriteLine($"{passed} passed, {failed} failed"); return failed == 0 ? 0 : 1; }
}
