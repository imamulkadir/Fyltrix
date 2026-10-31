// Test-owned processes only. No window, filesystem work, installer, or application profile access.
if (args.Length == 1 && args[0] == "--trial-test") return TrialStub.Run().GetAwaiter().GetResult();
if (args.Length == 2 && args[0] == "--test-key-root") return TestKeys.Create(args[1]);
if (args.Length != 1 || args[0] != "--test-wait") return 2;
System.Console.WriteLine("ready");
return System.Console.ReadLine() == "exit" ? 0 : 3;
