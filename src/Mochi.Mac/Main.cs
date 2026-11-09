using AppKit;
using Mochi.Mac;

NSApplication.Init();
NSApplication.SharedApplication.Delegate = new AppDelegate();
NSApplication.Main(args);
