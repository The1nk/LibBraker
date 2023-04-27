using LibBraker;
using Serilog;

if (args.Length == 0)
    args = new string[] { "/help" };


PowerArgs.Args.InvokeMain<Args>(args);