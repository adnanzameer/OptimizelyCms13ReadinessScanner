using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests;

public class ApiSurfaceTests
{
    private static ApiSurface Surface()
    {
        var image = StubAssembly.Emit("EPiServer", """
            namespace EPiServer.Core
            {
                public class ContentArea
                {
                    public object Items => null!;
                    public int Count;
                    protected void Prot() { }
                    private void Priv() { }
                    internal void Int() { }
                    public class Nested { public void N() { } }
                }
                class Hidden { public void M() { } }
                public interface IBase { void FromBase(); }
                public interface IDerived : IBase { void Own(); }
                public class BaseClass { public void Inherited() { } }
                public class Child : BaseClass { }
                public class Generic<T> { public void G() { } }
                public class GChild : Generic<int> { }
                public class Holder { class PrivateNested { public void X() { } } }
            }
            """);
        var surface = new ApiSurface();
        Assert.True(surface.AddAssembly(image));
        return surface;
    }

    [Fact]
    public void Public_types_are_indexed_and_internal_ones_are_not()
    {
        var s = Surface();

        Assert.True(s.HasType("EPiServer.Core.ContentArea"));
        Assert.False(s.HasType("EPiServer.Core.Hidden"));
        Assert.True(s.HasType("EPiServer.Core.ContentArea+Nested"));
        Assert.False(s.HasType("EPiServer.Core.Holder+PrivateNested"));
        Assert.Contains("EPiServer", s.AssemblyNames);
    }

    [Fact]
    public void Generic_types_use_the_metadata_arity_suffix()
    {
        Assert.True(Surface().HasType("EPiServer.Core.Generic`1"));
    }

    [Theory]
    [InlineData("Items", MemberLookup.Found)]
    [InlineData("Count", MemberLookup.Found)]        // field
    [InlineData("Prot", MemberLookup.Found)]         // protected is API
    [InlineData("Priv", MemberLookup.NotFound)]      // private is not
    [InlineData("Int", MemberLookup.NotFound)]       // internal is not
    [InlineData("FilteredItems", MemberLookup.NotFound)]
    [InlineData("get_Items", MemberLookup.Found)]    // accessor methods are present too
    public void Member_lookup_on_a_concrete_type(string member, MemberLookup expected)
    {
        Assert.Equal(expected, Surface().LookupMember("EPiServer.Core.ContentArea", member));
    }

    [Fact]
    public void Inherited_members_are_found_through_base_classes_interfaces_and_generic_bases()
    {
        var s = Surface();

        Assert.Equal(MemberLookup.Found, s.LookupMember("EPiServer.Core.Child", "Inherited"));
        Assert.Equal(MemberLookup.Found, s.LookupMember("EPiServer.Core.IDerived", "FromBase"));
        Assert.Equal(MemberLookup.Found, s.LookupMember("EPiServer.Core.GChild", "G"));        // Generic<int> base decoded
        Assert.Equal(MemberLookup.NotFound, s.LookupMember("EPiServer.Core.Child", "Missing"));
    }

    [Fact]
    public void Unknown_type_gives_Unknown_not_NotFound()
    {
        Assert.Equal(MemberLookup.Unknown, Surface().LookupMember("EPiServer.Core.Nope", "X"));
    }

    private static ApiSurface ObsoleteSurface()
    {
        var surface = new ApiSurface();
        Assert.True(surface.AddAssembly(StubAssembly.Emit("EPiServer", """
            using System;
            namespace EPiServer.Core
            {
                public class ContentArea
                {
                    [Obsolete("Use Items instead.", true)] public object FilteredItems => null!;
                    [Obsolete("Going away.")] public void Old() { }
                    [Obsolete] public void Bare() { }
                    [Obsolete("only this overload", true)] public void Mixed(int x) { }
                    public void Mixed(string x) { }
                    public object Items => null!;
                }
                [Obsolete("Type is gone.", true)] public class DeadType { }
            }
            """)));
        return surface;
    }

    [Fact]
    public void Obsolete_with_error_true_is_reported_with_its_message()
    {
        var result = ObsoleteSurface().Inspect("EPiServer.Core.ContentArea", "FilteredItems");

        Assert.Equal(MemberLookup.Found, result.Lookup);
        Assert.NotNull(result.Obsolete);
        Assert.True(result.Obsolete!.IsError);
        Assert.Equal("Use Items instead.", result.Obsolete.Message);
    }

    [Fact]
    public void Obsolete_without_error_is_a_warning_level_obsolete_and_a_bare_attribute_has_no_message()
    {
        var s = ObsoleteSurface();

        var old = s.Inspect("EPiServer.Core.ContentArea", "Old").Obsolete!;
        Assert.False(old.IsError);
        Assert.Equal("Going away.", old.Message);

        var bare = s.Inspect("EPiServer.Core.ContentArea", "Bare").Obsolete!;
        Assert.False(bare.IsError);
        Assert.Null(bare.Message);
    }

    [Fact]
    public void A_name_is_only_obsolete_when_every_overload_is()
    {
        Assert.Null(ObsoleteSurface().Inspect("EPiServer.Core.ContentArea", "Mixed").Obsolete);
    }

    [Fact]
    public void Non_obsolete_members_and_obsolete_types_are_distinguished()
    {
        var s = ObsoleteSurface();

        Assert.Null(s.Inspect("EPiServer.Core.ContentArea", "Items").Obsolete);
        Assert.True(s.TypeObsolete("EPiServer.Core.DeadType")!.IsError);
        Assert.Null(s.TypeObsolete("EPiServer.Core.ContentArea"));
    }

    [Fact]
    public void Non_assembly_bytes_are_rejected()
    {
        Assert.False(new ApiSurface().AddAssembly(new byte[] { 1, 2, 3, 4 }));
    }
}
