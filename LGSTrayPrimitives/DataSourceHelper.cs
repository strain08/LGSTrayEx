namespace LGSTrayPrimitives;

public static class DataSourceHelper
{
    /// <summary>
    /// Determines the data source based on device ID pattern.
    /// GHub devices have "GHUB." prefix, Native devices use hex format.
    /// </summary>
    public static DataSource GetDataSource(string deviceId)
    {
        return deviceId.StartsWith("GHUB.") ? DataSource.GHub : DataSource.Native;
    }
}
