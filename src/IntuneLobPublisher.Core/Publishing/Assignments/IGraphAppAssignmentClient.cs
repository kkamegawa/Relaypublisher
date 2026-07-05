namespace IntuneLobPublisher.Core.Publishing.Assignments;

/// <summary>Reads and writes one app's Intune assignments via Graph.</summary>
public interface IGraphAppAssignmentClient
{
    /// <summary>Lists the app's current assignments in canonical form.</summary>
    Task<IReadOnlyList<CurrentAssignment>> GetAssignmentsAsync(string appId, CancellationToken cancellationToken);

    /// <summary>Creates a new assignment.</summary>
    Task CreateAssignmentAsync(string appId, DesiredAssignment assignment, bool isWin32, CancellationToken cancellationToken);

    /// <summary>Updates an existing assignment to the desired intent/filter/settings.</summary>
    Task UpdateAssignmentAsync(string appId, string assignmentId, DesiredAssignment assignment, bool isWin32, CancellationToken cancellationToken);

    /// <summary>Deletes an existing assignment.</summary>
    Task DeleteAssignmentAsync(string appId, string assignmentId, CancellationToken cancellationToken);
}
