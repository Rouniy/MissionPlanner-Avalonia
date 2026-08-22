using System.Net;
using System.Text;
using System.Xml;
using BruTile;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class OgcMapProviderTests {
  [Fact]
  public async Task Wms_discovery_preserves_auth_query_and_inherits_parent_crs() {
    var handler = new StaticResponseHandler(WmsCapabilities("1.3.0", "CRS", "EPSG:3857"));
    using var client = new HttpClient(handler);

    WmsDiscovery discovery = await OgcMapProvider.DiscoverWmsAsync(
        "https://maps.example.test/wms?token=secret&request=Old", client);

    WmsLayerChoice layer = Assert.Single(discovery.Layers);
    Assert.Equal("flight-map", layer.Name);
    Assert.Equal("Flight Map", layer.Title);
    Assert.Equal("EPSG:3857", layer.Crs);
    Assert.NotNull(handler.LastRequest);
    Dictionary<string, string> query = Query(handler.LastRequest!);
    Assert.Equal("secret", query["token"]);
    Assert.Equal("WMS", query["SERVICE"]);
    Assert.Equal("GetCapabilities", query["REQUEST"]);
    Assert.DoesNotContain("Old", handler.LastRequest!.Query, StringComparison.Ordinal);
    Assert.True(handler.SawUserAgent);
  }

  [Fact]
  public async Task Wms_discovery_rejects_dtd_before_any_entity_is_expanded() {
    const string xml = """
        <!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
        <WMT_MS_Capabilities version="1.1.1"><Service>&xxe;</Service></WMT_MS_Capabilities>
        """;
    using var client = new HttpClient(new StaticResponseHandler(xml));

    await Assert.ThrowsAsync<XmlException>(() =>
        OgcMapProvider.DiscoverWmsAsync("https://maps.example.test/wms", client));
  }

  [Fact]
  public void Wms_111_uses_longitude_latitude_bbox_and_preserves_tokens() {
    Uri uri = OgcMapProvider.BuildWmsTileUri(
        "https://maps.example.test/wms?token=abc&REQUEST=GetCapabilities",
        "roads & labels", "1.1.1", "EPSG:4326", new TileIndex(1, 0, 1));

    Dictionary<string, string> query = Query(uri);
    Assert.Equal("abc", query["token"]);
    Assert.Equal("GetMap", query["REQUEST"]);
    Assert.Equal("roads & labels", query["LAYERS"]);
    Assert.Equal("EPSG:4326", query["SRS"]);
    Assert.False(query.ContainsKey("CRS"));
    double[] bbox = Coordinates(query["BBOX"]);
    Assert.Equal(0, bbox[0], 10);
    Assert.Equal(0, bbox[1], 10);
    Assert.Equal(180, bbox[2], 10);
    Assert.Equal(85.0511287798066, bbox[3], 10);
  }

  [Fact]
  public void Wms_130_epsg4326_uses_latitude_first_axis_order() {
    Uri uri = OgcMapProvider.BuildWmsTileUri(
        "https://maps.example.test/wms", "base", "1.3.0", "EPSG:4326",
        new TileIndex(1, 0, 1));

    Dictionary<string, string> query = Query(uri);
    Assert.Equal("EPSG:4326", query["CRS"]);
    Assert.False(query.ContainsKey("SRS"));
    double[] bbox = Coordinates(query["BBOX"]);
    Assert.Equal(0, bbox[0], 10);
    Assert.Equal(0, bbox[1], 10);
    Assert.Equal(85.0511287798066, bbox[2], 10);
    Assert.Equal(180, bbox[3], 10);
  }

  [Fact]
  public void Wms_web_mercator_uses_projected_tile_bounds() {
    Uri uri = OgcMapProvider.BuildWmsTileUri(
        "https://maps.example.test/wms", "base", "1.3.0", "EPSG:3857",
        new TileIndex(0, 0, 0));

    double[] bbox = Coordinates(Query(uri)["BBOX"]);
    Assert.Equal(-20037508.342789244, bbox[0], 6);
    Assert.Equal(-20037508.342789244, bbox[1], 6);
    Assert.Equal(20037508.342789244, bbox[2], 6);
    Assert.Equal(20037508.342789244, bbox[3], 6);
  }

  [Fact]
  public async Task Wmts_discovery_returns_only_compatible_web_mercator_sources() {
    var handler = new StaticResponseHandler(WmtsCapabilities);
    using var client = new HttpClient(handler);

    WmtsDiscovery discovery = await OgcMapProvider.DiscoverWmtsAsync(
        "https://maps.example.test/WMTSCapabilities.xml", client);

    WmtsLayerChoice layer = Assert.Single(discovery.Layers);
    Assert.Equal(0, layer.SourceIndex);
    Assert.Contains("Test Basemap", layer.DisplayName, StringComparison.Ordinal);
    Assert.Equal(64, discovery.CapabilitiesHash.Length);
    Assert.True(discovery.Capabilities.Length > 0);
  }

  [Fact]
  public async Task Wmts_discovery_rejects_shifted_matrix_levels_that_would_misaddress_tiles() {
    string shifted = WmtsCapabilities
        .Replace("559082264.029", "279541132.015", StringComparison.Ordinal)
        .Replace("<MatrixWidth>1</MatrixWidth><MatrixHeight>1</MatrixHeight>",
            "<MatrixWidth>2</MatrixWidth><MatrixHeight>2</MatrixHeight>",
            StringComparison.Ordinal);
    using var client = new HttpClient(new StaticResponseHandler(shifted));

    InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
        OgcMapProvider.DiscoverWmtsAsync(
            "https://maps.example.test/WMTSCapabilities.xml", client));

    Assert.Contains("no Web Mercator image layer", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Capabilities_download_is_bounded_even_without_content_length() {
    byte[] oversized = new byte[OgcMapProvider.MaximumCapabilitiesBytes + 1];
    using var client = new HttpClient(new ByteResponseHandler(oversized));

    InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
        OgcMapProvider.DiscoverWmsAsync("https://maps.example.test/wms", client));

    Assert.Contains("larger than 8 MiB", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Existing_upstream_wms_settings_activate_the_shared_provider() {
    string? server = Settings.Instance["WMSserver"];
    string? layer = Settings.Instance["WMSLayer"];
    string? version = Settings.Instance["WMSVersion"];
    string? crs = Settings.Instance["WMSCrs"];
    try {
      Settings.Instance["WMSserver"] = "https://maps.example.test/wms";
      Settings.Instance["WMSLayer"] = "official-layer";
      Settings.Instance["WMSVersion"] = null;
      Settings.Instance["WMSCrs"] = null;

      Assert.Equal(OgcMapProvider.WmsMapType,
          MapTileSourceFactory.NormalizeMapType(OgcMapProvider.WmsMapType));
      Assert.Equal(OgcMapProvider.WmsMapType,
          MapTileSourceFactory.CreateMapLayer(OgcMapProvider.WmsMapType).Name);
    } finally {
      Settings.Instance["WMSserver"] = server;
      Settings.Instance["WMSLayer"] = layer;
      Settings.Instance["WMSVersion"] = version;
      Settings.Instance["WMSCrs"] = crs;
    }
  }

  private static string WmsCapabilities(string version, string crsElement, string crs) => $$"""
      <?xml version="1.0" encoding="UTF-8"?>
      <WMS_Capabilities version="{{version}}" xmlns="http://www.opengis.net/wms">
        <Capability>
          <Request><GetMap><Format>image/png</Format></GetMap></Request>
          <Layer>
            <Title>Root</Title><{{crsElement}}>{{crs}}</{{crsElement}}>
            <Layer><Name>flight-map</Name><Title>Flight Map</Title></Layer>
          </Layer>
        </Capability>
      </WMS_Capabilities>
      """;

  private const string WmtsCapabilities = """
      <?xml version="1.0" encoding="UTF-8"?>
      <Capabilities xmlns="http://www.opengis.net/wmts/1.0"
          xmlns:ows="http://www.opengis.net/ows/1.1"
          xmlns:xlink="http://www.w3.org/1999/xlink" version="1.0.0">
        <ows:ServiceIdentification>
          <ows:Title>Test WMTS</ows:Title>
          <ows:ServiceType>OGC WMTS</ows:ServiceType>
          <ows:ServiceTypeVersion>1.0.0</ows:ServiceTypeVersion>
        </ows:ServiceIdentification>
        <ows:OperationsMetadata />
        <Contents>
          <Layer>
            <ows:Title>Test Basemap</ows:Title>
            <ows:Identifier>test-base</ows:Identifier>
            <Style isDefault="true"><ows:Identifier>default</ows:Identifier></Style>
            <Format>image/png</Format>
            <TileMatrixSetLink><TileMatrixSet>google3857</TileMatrixSet></TileMatrixSetLink>
            <ResourceURL format="image/png" resourceType="tile"
                template="https://tiles.example.test/{Style}/{TileMatrixSet}/{TileMatrix}/{TileRow}/{TileCol}.png" />
          </Layer>
          <TileMatrixSet>
            <ows:Identifier>google3857</ows:Identifier>
            <ows:SupportedCRS>urn:ogc:def:crs:EPSG:6.18.3:3857</ows:SupportedCRS>
            <WellKnownScaleSet>urn:ogc:def:wkss:OGC:1.0:GoogleMapsCompatible</WellKnownScaleSet>
            <TileMatrix>
              <ows:Identifier>0</ows:Identifier>
              <ScaleDenominator>559082264.029</ScaleDenominator>
              <TopLeftCorner>-20037508.3428 20037508.3428</TopLeftCorner>
              <TileWidth>256</TileWidth><TileHeight>256</TileHeight>
              <MatrixWidth>1</MatrixWidth><MatrixHeight>1</MatrixHeight>
            </TileMatrix>
            <TileMatrix>
              <ows:Identifier>1</ows:Identifier>
              <ScaleDenominator>279541132.015</ScaleDenominator>
              <TopLeftCorner>-20037508.3428 20037508.3428</TopLeftCorner>
              <TileWidth>256</TileWidth><TileHeight>256</TileHeight>
              <MatrixWidth>2</MatrixWidth><MatrixHeight>2</MatrixHeight>
            </TileMatrix>
          </TileMatrixSet>
        </Contents>
      </Capabilities>
      """;

  private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?')
      .Split('&', StringSplitOptions.RemoveEmptyEntries)
      .Select(part => part.Split('=', 2))
      .ToDictionary(
          part => Uri.UnescapeDataString(part[0]),
          part => part.Length == 1 ? "" : Uri.UnescapeDataString(part[1]),
          StringComparer.OrdinalIgnoreCase);

  private static double[] Coordinates(string value) => value.Split(',')
      .Select(item => double.Parse(item, System.Globalization.CultureInfo.InvariantCulture))
      .ToArray();

  private sealed class StaticResponseHandler(string response)
      : ByteResponseHandler(Encoding.UTF8.GetBytes(response));

  private class ByteResponseHandler(byte[] response) : HttpMessageHandler {
    public Uri? LastRequest { get; private set; }
    public bool SawUserAgent { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) {
      LastRequest = request.RequestUri;
      SawUserAgent = request.Headers.UserAgent.Count > 0;
      var content = new StreamContent(new MemoryStream(response, writable: false));
      // Deliberately omit Content-Length to exercise the streaming size guard.
      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
  }
}
