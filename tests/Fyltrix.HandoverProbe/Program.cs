// Test-owned processes only. No window, filesystem work, installer, or application profile access.
if (args.Length != 1 || args[0] != "--test-wait") return 2;
System.Console.WriteLine("ready");
return System.Console.ReadLine() == "exit" ? 0 : 3;
