using System.Reflection;

namespace Fyltrix.Core;

/// <summary>The display name comes from product metadata; executable and storage identities are independent.</summary>
internal static class ProductBranding
{
    public static string Name { get; } = typeof(ProductBranding).Assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product;
}
