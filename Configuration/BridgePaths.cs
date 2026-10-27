internal static class BridgePaths
{
  internal static string ResolveRuntimePath(string path)
  {
    if (Path.IsPathRooted(path))
    {
      return path;
    }
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
  }
}
