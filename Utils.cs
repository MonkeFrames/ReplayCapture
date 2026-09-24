namespace MonkeFrames.ReplayCapture;

public static class Utils
{
    public static string Combine(params string[] path)
    {
        char pathSeperator = '\\';
        string final = path[0];

        foreach (string p in path[1..])
            final += pathSeperator + p;

        return final;
    }
}