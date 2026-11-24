import argparse
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


REQUIRED_INTEGRATION = {
    "BackEnd.Tests": (
        "HabitsHttpTests.Habits_RequireJwt_AndHideOtherUsersHabits",
        "HabitsHttpTests.Outbox_RecordsOnlyChangesAndRollsBackWhenEventCannotBeInserted",
        "HabitsHttpTests.RabbitOutbox_DeliversAfterBrokerRecoveryAndRepeatsWithOriginalIdentity",
    ),
    "HabitTracker.Worker.Tests": (
        "ScheduleMessagesTests.DuplicateAndOldEventsDoNotOverwriteArchive",
        "ScheduleMessagesTests.BootstrapRetriesAndOlderPageCannotOverwriteBrokersEvent",
        "ExpiredOwnerCannotCompleteTests.ExpiredOwnerCannotFinishJob",
        "OutboxFailureRollsBackCompletionTests.FailedOutboxInsertRollsBackAndAnotherOwnerCanFinish",
    ),
    "HabitTracker.Notifications.Tests": (
        "NotificationsIntegrationTests.HistoryPaginatesByTimeAndIdAndReadIsOwnerScopedAndIdempotent",
        "NotificationsIntegrationTests.JwtProtectsRestAndWebSocketAndHubRoutesToAllConnectionsOfTheOwner",
        "NotificationsIntegrationTests.QueueKeepsOfflineMessagesRetriesHubFailureAndReplaysFromErrorQueue",
    ),
}


def read_test_names(path):
    results = ET.parse(path).findall(".//{*}Results/{*}UnitTestResult")
    if not results:
        raise ValueError(f"No test results in {path}")
    for result in results:
        if result.attrib["outcome"] != "Passed":
            raise ValueError(f'{result.attrib["testName"]}: {result.attrib["outcome"]}')
    return {result.attrib["testName"].split("(", 1)[0] for result in results}


def check_tests(root):
    for project, required in REQUIRED_INTEGRATION.items():
        unit = read_test_names(root / "unit" / project / "tests.trx")
        integration = read_test_names(root / "integration" / project / "tests.trx")
        for test in required:
            if any(name.endswith("." + test) for name in unit):
                raise ValueError(f"{test} was included in the unit suite")
            if not any(name.endswith("." + test) for name in integration):
                raise ValueError(f"Missing integration test in {project}: {test}")
        print(f"{project}: {len(unit)} unit and {len(integration)} integration scenarios passed")


def coverage_lines(root):
    covered = {}
    for project in REQUIRED_INTEGRATION:
        # VSTest also copies the attachment under the TRX deployment directory.
        reports = sorted((root / "unit" / project).glob("*/coverage.cobertura.xml"))
        if len(reports) != 1:
            raise ValueError(f"Expected one unit coverage report for {project}, got {len(reports)}")
        classes = ET.parse(reports[0]).findall(".//class")
        if not any(cls.findall("./lines/line") for cls in classes):
            raise ValueError(f"No source lines in the coverage report for {project}")
        for cls in classes:
            filename = cls.attrib["filename"].replace("\\", "/")
            for line in cls.findall("./lines/line"):
                key = (filename, int(line.attrib["number"]))
                covered[key] = covered.get(key, False) or int(line.attrib["hits"]) > 0
    return sum(covered.values()), len(covered)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("report_dir", type=Path)
    parser.add_argument("--enforce-coverage", action="store_true")
    args = parser.parse_args()
    try:
        check_tests(args.report_dir)
        covered, total = coverage_lines(args.report_dir)
        print(f"Unit line coverage: {covered}/{total} ({100 * covered / total:.2f}%)")
        if covered * 100 <= total * 60:
            message = "Unit line coverage must be strictly above 60%."
            if args.enforce_coverage:
                raise ValueError(message)
            print(f"::warning::{message} The threshold is currently report-only.")
    except (OSError, ET.ParseError, KeyError, ValueError) as error:
        print(f"CI report validation failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
