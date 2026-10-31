namespace Fyltrix.Core.Tools;

public sealed class SizeNode
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; set; }
    public int Files { get; set; }
    public int Folders { get; set; }
    /// <summary>Junction / symbolic link: shown but not followed (it would double-count or loop).</summary>
    public bool IsLink { get; init; }
    public bool Inaccessible { get; set; }
    public SizeNode? Parent { get; init; }
    public List<SizeNode> Children { get; } = new();

    public double ShareOfParent => Parent == null || Parent.Size == 0 ? 1 : (double)Size / Parent.Size;
}

public sealed class FolderReport
{
    public required SizeNode Root { get; init; }
    public List<(string path, long size)> LargestFiles { get; } = new();
    public int InaccessibleFolders { get; set; }
    public int LinksSkipped { get; set; }
}

/// <summary>
/// Sizes a folder tree. Logical file sizes are counted (like Explorer's "Size", not "Size on disk").
/// Junctions and symbolic links are listed but never followed, so nothing is counted twice and loops are impossible.
/// </summary>
public static class FolderAnalyzer
{
    public static FolderReport Analyze(string root, int largestFiles = 100, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        root = Path.GetFullPath(root);
        var top = new SizeNode { Name = Path.GetFileName(root.TrimEnd('\\')) is { Length: > 0 } n ? n : root, Path = root, IsDirectory = true };
        var report = new FolderReport { Root = top };
        var largest = new PriorityQueue<string, long>();
        int scanned = 0;
        var lastReport = DateTime.UtcNow;

        void Walk(SizeNode node)
        {
            ct.ThrowIfCancellationRequested();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(node.Path).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false }).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                node.Inaccessible = true;
                report.InaccessibleFolders++;
                return;
            }
            foreach (var e in entries)
            {
                bool isDir = (e.Attributes & FileAttributes.Directory) != 0;
                bool isLink = (e.Attributes & FileAttributes.ReparsePoint) != 0;
                if (isDir)
                {
                    var child = new SizeNode { Name = e.Name, Path = e.FullName, IsDirectory = true, IsLink = isLink, Parent = node };
                    node.Children.Add(child);
                    node.Folders++;
                    if (isLink) { report.LinksSkipped++; continue; }
                    Walk(child);
                    node.Size += child.Size;
                    node.Files += child.Files;
                    node.Folders += child.Folders;
                }
                else
                {
                    long len = 0;
                    try { len = ((FileInfo)e).Length; } catch (IOException) { }
                    node.Children.Add(new SizeNode { Name = e.Name, Path = e.FullName, Size = len, Parent = node, IsLink = isLink });
                    node.Size += len;
                    node.Files++;
                    largest.Enqueue(e.FullName, len);
                    if (largest.Count > largestFiles) largest.Dequeue();
                    if (++scanned % 2000 == 0 && (DateTime.UtcNow - lastReport).TotalMilliseconds > 150)
                    {
                        lastReport = DateTime.UtcNow;
                        progress?.Report(new OperationProgress("Measuring folders…", -1, $"{scanned:N0} files · {node.Path}"));
                    }
                }
            }
            node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        }

        Walk(top);
        while (largest.TryDequeue(out var p, out var s)) report.LargestFiles.Add((p, s));
        report.LargestFiles.Reverse();
        return report;
    }

    /// <summary>
    /// Squarified treemap layout (Bruls et al.) of a node's children inside a rectangle.
    /// Returns rectangles in the same order as the input sizes; zero-size items get empty rectangles.
    /// </summary>
    public static List<(double x, double y, double w, double h)> Treemap(IReadOnlyList<long> sizes, double width, double height)
    {
        var result = new (double, double, double, double)[sizes.Count];
        var order = Enumerable.Range(0, sizes.Count).Where(i => sizes[i] > 0).OrderByDescending(i => sizes[i]).ToList();
        double total = order.Sum(i => (double)sizes[i]);
        if (total <= 0 || width <= 0 || height <= 0) return result.ToList();
        double scale = width * height / total;
        double x = 0, y = 0, w = width, h = height;
        int start = 0;
        while (start < order.Count)
        {
            double side = Math.Min(w, h);
            int end = start;
            double rowSum = 0, best = double.MaxValue;
            while (end < order.Count)
            {
                double area = sizes[order[end]] * scale;
                double newSum = rowSum + area;
                double maxA = Math.Max(area, end > start ? order.Skip(start).Take(end - start).Max(i => sizes[i] * scale) : area);
                double minA = Math.Min(area, end > start ? order.Skip(start).Take(end - start).Min(i => sizes[i] * scale) : area);
                double worst = Math.Max(side * side * maxA / (newSum * newSum), newSum * newSum / (side * side * minA));
                if (worst > best) break;
                best = worst;
                rowSum = newSum;
                end++;
            }
            double thickness = rowSum / side;
            double offset = 0;
            for (int k = start; k < end; k++)
            {
                double len = sizes[order[k]] * scale / thickness;
                result[order[k]] = w >= h ? (x, y + offset, thickness, len) : (x + offset, y, len, thickness);
                offset += len;
            }
            if (w >= h) { x += thickness; w -= thickness; } else { y += thickness; h -= thickness; }
            start = end;
        }
        return result.ToList();
    }
}
