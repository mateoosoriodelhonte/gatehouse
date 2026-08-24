namespace Gatehouse.Web.Ui;

public sealed class UiSessionState
{
    private Guid? repositoryId;

    public Guid? RepositoryId => repositoryId;

    public HashSet<int> SelectedPullRequests { get; } = [];

    public void SelectRepository(Guid value)
    {
        if (repositoryId != value)
        {
            SelectedPullRequests.Clear();
        }

        repositoryId = value;
    }

    public void SetPullRequestSelected(int number, bool selected)
    {
        if (selected)
        {
            SelectedPullRequests.Add(number);
        }
        else
        {
            SelectedPullRequests.Remove(number);
        }
    }
}
