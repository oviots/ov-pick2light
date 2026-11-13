using P2L.Shared.Models;

namespace P2L.Shared;

/// <summary>The controller must not be acknowledged until Record has committed.</summary>
public interface IConfirmationJournal
{
    bool HasRecorded(string pickingDetailId);
    void Record(ConfirmationEvent confirmation, LightModuleState instruction);
}
