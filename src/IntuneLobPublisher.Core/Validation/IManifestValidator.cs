using FluentValidation.Results;
using IntuneLobPublisher.Core.Manifests;

namespace IntuneLobPublisher.Core.Validation;

/// <summary>Validates a single loaded manifest.</summary>
public interface IManifestValidator
{
    ValidationResult Validate(IntunePackageManifest manifest);
}
