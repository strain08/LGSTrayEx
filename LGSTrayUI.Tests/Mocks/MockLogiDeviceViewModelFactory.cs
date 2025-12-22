namespace LGSTrayUI.Tests.Mocks;

/// <summary>
/// Mock factory for creating LogiDeviceViewModel instances in tests
/// </summary>
public class MockLogiDeviceViewModelFactory
{
    private readonly LogiDeviceIconFactory _iconFactory;

    public MockLogiDeviceViewModelFactory(LogiDeviceIconFactory iconFactory)
    {
        _iconFactory = iconFactory;
    }

    public LogiDeviceViewModel CreateViewModel(Action<LogiDeviceViewModel>? config = null)
    {
        var viewModel = new LogiDeviceViewModel(_iconFactory);
        config?.Invoke(viewModel);
        return viewModel;
    }
}
