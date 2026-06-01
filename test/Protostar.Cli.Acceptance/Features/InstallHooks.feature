Feature: Installing capture hooks into a harness
  As an operator
  I want protostar install-hooks to wire capture into my harness idempotently
  So that skill use is captured automatically without manual editing

  Scenario Outline: Installing hooks writes the capture entries
    Given a fake <harness> harness
    When I run protostar with "install-hooks --harness <harness> --harness-home {harnessHome} --yes"
    Then the exit code is 0
    And the harness settings contain "PostToolUse"
    And the harness settings contain "SessionStart"
    And the harness settings contain "capture --hook"

    Examples:
      | harness     |
      | claude-code |

  Scenario Outline: Re-running install-hooks is idempotent
    Given a fake <harness> harness
    When I run protostar with "install-hooks --harness <harness> --harness-home {harnessHome} --yes"
    And I run protostar with "install-hooks --harness <harness> --harness-home {harnessHome} --yes"
    Then the exit code is 0
    And the harness has 1 protostar PostToolUse hooks

    Examples:
      | harness     |
      | claude-code |

  Scenario Outline: Existing settings and user hooks are preserved
    Given a fake <harness> harness with settings:
      """
      {
        "model": "opus",
        "hooks": {
          "PostToolUse": [
            { "matcher": "Bash", "hooks": [ { "type": "command", "command": "echo mine" } ] }
          ]
        }
      }
      """
    When I run protostar with "install-hooks --harness <harness> --harness-home {harnessHome} --yes"
    Then the exit code is 0
    And the harness settings contain "opus"
    And the harness settings contain "echo mine"
    And the harness settings contain "capture --hook"
    And the harness has 1 protostar PostToolUse hooks

    Examples:
      | harness     |
      | claude-code |

  Scenario Outline: A dry run writes nothing
    Given a fake <harness> harness
    When I run protostar with "install-hooks --harness <harness> --harness-home {harnessHome} --yes --dry-run"
    Then the exit code is 0
    And the harness has no settings file

    Examples:
      | harness     |
      | claude-code |

  Scenario Outline: Removing hooks leaves other settings intact
    Given a fake <harness> harness with settings:
      """
      { "model": "opus" }
      """
    When I run protostar with "install-hooks --harness <harness> --harness-home {harnessHome} --yes"
    And I run protostar with "install-hooks --harness <harness> --harness-home {harnessHome} --yes --remove"
    Then the exit code is 0
    And the harness settings contain "opus"
    And the harness has 0 protostar PostToolUse hooks

    Examples:
      | harness     |
      | claude-code |

  Scenario: The capture command acknowledges a hook event
    When I run protostar with "capture --hook PostToolUse"
    Then the exit code is 0
    And the output contains "protostar capture"
