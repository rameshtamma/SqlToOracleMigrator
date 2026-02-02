// Intentionally empty partial.
//
// Constructors for PasswordPromptWindow are defined in PasswordPromptWindow.xaml.cs.
// A prior patch added constructor overloads here as well, which can lead to
// duplicate/ambiguous constructor definitions (CS0111/CS0121) depending on the
// current code-behind.
//
// Keeping this file (empty) avoids breaking existing project includes while
// ensuring there is a single source of truth for constructors.

namespace SqlToOracleMigrator.Desktop.Views;

public partial class PasswordPromptWindow
{
    // No constructors in this partial file.
}
