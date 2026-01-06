using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace LGSTrayUI
{
    internal class Program
    {
        [STAThread]
                        static void Main()
                        {
                            // TODO Whatever you want to do before starting
                            // the WPF application and loading all WPF dlls
                            try
                            {
                                RunApp();
                            }            catch (Exception ex)
            {
                try
                {
                    var unixTime = DateTimeOffset.Now.ToUnixTimeSeconds();
                    string crashFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"LGSTrayUI_Crash_{unixTime}.txt");
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"[{DateTime.Now}] STARTUP CRASH OCCURRED");
                    
                    // Recursively log exceptions
                    Exception? currentEx = ex;
                    int depth = 0;
                    while (currentEx != null)
                    {
                        string prefix = depth == 0 ? "Exception" : $"Inner Exception [{depth}]";
                        sb.AppendLine($"{prefix}: {currentEx.GetType().Name}: {currentEx.Message}");
                        sb.AppendLine($"Stack Trace:\n{currentEx.StackTrace}");
                        
                        if (currentEx is AggregateException aggEx)
                        {
                            sb.AppendLine($"Flattened AggregateException Details:");
                            foreach (var inner in aggEx.Flatten().InnerExceptions)
                            {
                                sb.AppendLine($"- {inner.GetType().Name}: {inner.Message}");
                            }
                        }

                        currentEx = currentEx.InnerException;
                        depth++;
                        if (currentEx != null) sb.AppendLine();
                    }

                    sb.AppendLine("\nFull ToString():");
                    sb.AppendLine(ex.ToString());
                    sb.AppendLine("--------------------------------------------------");
                    
                    File.AppendAllText(crashFile, sb.ToString());
                }
                catch
                {
                    // If logging fails, there's not much we can do.
                }
            }
        }

        // Ensure the method is not inlined, so you don't
        // need to load any WPF dll in the Main method
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        static void RunApp()
        {
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}
