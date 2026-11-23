using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace WinRtspPlayer.Tests;

public class SmokeTests
{
    [Fact]
    public void Runtime_IsDotNet8OrHigher()
    {
        Assert.True(Environment.Version.Major >= 8, $"Expected .NET 8+, got {Environment.Version}.");
    }

    [Fact]
    public void CoreAssembly_LoadsCleanly()
    {
        var assembly = Assembly.Load("WinRtspPlayer.Core");
        Assert.NotNull(assembly);
    }

    [Fact]
    public void NetSdkMetadata_UsesCdecl_VoidCloseStream_FreeInitKey_AndNativeDllLoader()
    {
        string dllPath = GetNetSdkDllPath();
        using var stream = File.OpenRead(dllPath);
        using var peReader = new PEReader(stream);
        MetadataReader metadata = peReader.GetMetadataReader();

        TypeDefinition playerSdk = FindType(metadata, "EasyPlayerNetSDK", "PlayerSdk");
        int pInvokeCount = 0;
        MethodDefinition? closeStream = null;
        MethodDefinition? initMethod = null;

        foreach (MethodDefinitionHandle handle in playerSdk.GetMethods())
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            string name = metadata.GetString(method.Name);

            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
            {
                pInvokeCount++;
                MethodImport import = method.GetImport();
                MethodImportAttributes callingConv = import.Attributes & MethodImportAttributes.CallingConventionMask;
                Assert.Equal(MethodImportAttributes.CallingConventionCDecl, callingConv);
            }

            if (name == "EasyPlayer_CloseStream")
            {
                closeStream = method;
            }
            else if (name == "EasyPlayer_Init")
            {
                initMethod = method;
            }
        }

        Assert.True(pInvokeCount >= 9, $"Expected at least 9 P/Invoke methods on PlayerSdk, found {pInvokeCount}.");

        Assert.NotNull(closeStream);
        var closeSig = closeStream.Value.DecodeSignature(new PrimitiveReturnTypeProvider(), genericContext: null);
        Assert.Equal(PrimitiveTypeCode.Void, closeSig.ReturnType);

        Assert.NotNull(initMethod);
        ParameterHandle firstParamHandle = initMethod.Value.GetParameters().First();
        Parameter firstParam = metadata.GetParameter(firstParamHandle);
        ConstantHandle defaultHandle = firstParam.GetDefaultValue();
        Assert.False(defaultHandle.IsNil, "EasyPlayer_Init key parameter must have a default value.");
        Constant defaultConst = metadata.GetConstant(defaultHandle);
        Assert.Equal(ConstantTypeCode.String, defaultConst.TypeCode);
        BlobReader defaultReader = metadata.GetBlobReader(defaultConst.Value);
        string defaultKey = defaultReader.ReadUTF16(defaultReader.Length);
        Assert.Equal("EasyPlayer is Free!", defaultKey);

        TypeDefinition nativeDllLoader = FindType(metadata, "EasyPlayerNetSDK", "NativeDllLoader");
        MethodDefinition? configureMethod = nativeDllLoader.GetMethods()
            .Select(h => (MethodDefinition?)metadata.GetMethodDefinition(h))
            .FirstOrDefault(m => m is { } def && metadata.GetString(def.Name) == "ConfigureLibDirectory");

        Assert.False(configureMethod is null, "NativeDllLoader.ConfigureLibDirectory must exist.");
        var configureSig = configureMethod.Value.DecodeSignature(new PrimitiveReturnTypeProvider(), genericContext: null);
        Assert.Single(configureSig.ParameterTypes);
        Assert.Equal(PrimitiveTypeCode.String, configureSig.ParameterTypes[0]);
    }

    private static string GetNetSdkDllPath()
    {
        string configuration = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Parent!.Name;

        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "EasyPlayerNetSDK", "bin", configuration, "net8.0-windows", "EasyPlayer-RTSP.NetSDK.dll"));

        Assert.True(File.Exists(path), $"Expected built assembly at '{path}'.");
        return path;
    }

    private static TypeDefinition FindType(MetadataReader metadata, string expectedNamespace, string expectedName)
    {
        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
        {
            TypeDefinition typeDef = metadata.GetTypeDefinition(handle);
            if (metadata.GetString(typeDef.Namespace) == expectedNamespace &&
                metadata.GetString(typeDef.Name) == expectedName)
            {
                return typeDef;
            }
        }

        throw new Xunit.Sdk.XunitException($"Type '{expectedNamespace}.{expectedName}' was not found in metadata.");
    }

    private sealed class PrimitiveReturnTypeProvider : ISignatureTypeProvider<PrimitiveTypeCode?, object?>
    {
        public PrimitiveTypeCode? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode;
        public PrimitiveTypeCode? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => null;
        public PrimitiveTypeCode? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => null;
        public PrimitiveTypeCode? GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => null;
        public PrimitiveTypeCode? GetSZArrayType(PrimitiveTypeCode? elementType) => null;
        public PrimitiveTypeCode? GetArrayType(PrimitiveTypeCode? elementType, ArrayShape shape) => null;
        public PrimitiveTypeCode? GetByReferenceType(PrimitiveTypeCode? elementType) => null;
        public PrimitiveTypeCode? GetPointerType(PrimitiveTypeCode? elementType) => null;
        public PrimitiveTypeCode? GetFunctionPointerType(MethodSignature<PrimitiveTypeCode?> signature) => null;
        public PrimitiveTypeCode? GetGenericInstantiation(PrimitiveTypeCode? genericType, System.Collections.Immutable.ImmutableArray<PrimitiveTypeCode?> typeArguments) => null;
        public PrimitiveTypeCode? GetGenericMethodParameter(object? genericContext, int index) => null;
        public PrimitiveTypeCode? GetGenericTypeParameter(object? genericContext, int index) => null;
        public PrimitiveTypeCode? GetModifiedType(PrimitiveTypeCode? modifier, PrimitiveTypeCode? unmodifiedType, bool isRequired) => unmodifiedType;
        public PrimitiveTypeCode? GetPinnedType(PrimitiveTypeCode? elementType) => elementType;
    }
}
