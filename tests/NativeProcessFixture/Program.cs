var mode = args[0];
var state = args[1];
File.AppendAllText(state, "attempt\n");
var attempt = File.ReadAllLines(state).Length;
if (mode == "no-completion") await Task.Delay(Timeout.Infinite);
if (mode == "slow-success") await Task.Delay(1500);
Console.WriteLine("[INFO] Create font-embeded subtitle: test.ass");
Console.Out.Flush();
if (mode == "always-stall" || mode == "stall-once" && attempt == 1) await Task.Delay(Timeout.Infinite);
if (mode == "late-error")
{
    await Task.Delay(100);
    Console.Error.WriteLine("[ERROR] simulated failure after completion message");
    return 1;
}
return 0;
