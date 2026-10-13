"""SL-107 (review M1, O-016): the rows that plant the committed whole-C:\\ record must keep working once it exists.

Bug: S83-S86 and P36 of eng/rules-harness/plants.json are op new-file rows at .taad/slices/SL-089/c-drive-run.json, the file a
person's run produces. Planting._create marked a row PLANT-BROKEN ("already exists, so it cannot be created") when its target
was there, and the harness copies every tracked or untracked-not-ignored file into its scratch copy, so from the day the record
is committed every `harness.py` run over the plants scenario would exit 1 with five PLANT-BROKEN rows, on a slice that touched
none of them (Q-027 makes that run a required validate check for any slice that touches .taad/fitness.yaml).

Expected: a new-file row whose path is listed under awaiting_file plants over the existing file, and restore() writes its bytes
back (checked against their SHA-256). A new-file row at any other existing path is still PLANT-BROKEN.

This runs the real Planting and the real rows of plants.json on a tiny scratch tree: no engine, no build, well under a second.
Standard library only.

  python tests/bugs/SL-107-HarnessPlantsOverRecord.Tests.py

Exit 0 when every check held, 1 when any failed.
"""
import importlib.util
import os
import shutil
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True  # no __pycache__ in eng/rules-harness

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
PLANTS = os.path.join(REPO, "eng", "rules-harness", "plants.json")
RECORD = ".taad/slices/SL-089/c-drive-run.json"
IDS = ("S83", "S84", "S85", "S86", "P36")

_spec = importlib.util.spec_from_file_location("rules_harness", os.path.join(REPO, "eng", "rules-harness", "harness.py"))
harness = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(harness)


class PlantsOverTheRecord(unittest.TestCase):
    def setUp(self):
        self.tree = tempfile.mkdtemp(prefix="fsa-sl107-plants-")
        self.addCleanup(shutil.rmtree, self.tree, ignore_errors=True)
        self.rows = [r for r in harness.load_rows(PLANTS, "plants") if r["id"] in IDS]
        self.awaiting = {v.lower() for v in harness.load_awaiting(PLANTS).values()}
        self.record = os.path.join(self.tree, *RECORD.split("/"))

    def put_record(self, data: bytes):
        os.makedirs(os.path.dirname(self.record))
        with open(self.record, "wb") as handle:
            handle.write(data)

    def read_record(self) -> bytes:
        with open(self.record, "rb") as handle:
            return handle.read()

    def test_the_rows_are_all_there_and_all_at_the_awaiting_file(self):
        self.assertEqual(sorted(r["id"] for r in self.rows), sorted(IDS))
        for row in self.rows:
            self.assertEqual(row["op"], "new-file", row["id"])
            self.assertEqual(row["file"], RECORD, row["id"])
        self.assertIn(RECORD.lower(), self.awaiting)

    def test_rows_plant_over_an_existing_record_and_put_it_back(self):
        original = b'{\r\n  "rows": 0\r\n}\r\n'
        self.put_record(original)
        batches = harness.batches(self.rows)
        self.assertEqual(len(batches), len(IDS))  # a new-file row is exclusive: one batch each
        for batch in batches:
            planting = harness.Planting(self.tree, batch, self.awaiting)
            planting.apply()
            self.assertEqual(planting.broken, {}, "a row over an existing record is PLANT-BROKEN")
            self.assertNotEqual(self.read_record(), original, "the row's text is in the file while it is planted")
            self.assertEqual(planting.created, [], "the record was not made by the row, so restore must not delete it")
            self.assertIn(batch[0]["id"], planting.placed)
            planting.restore()  # raises CannotMeasure unless the bytes are back under their SHA-256
            self.assertEqual(self.read_record(), original)

    def test_rows_make_the_record_and_remove_it_while_it_is_absent(self):
        for batch in harness.batches(self.rows):
            planting = harness.Planting(self.tree, batch, self.awaiting)
            planting.apply()
            self.assertEqual(planting.broken, {})
            self.assertEqual(planting.created, [RECORD])
            self.assertTrue(os.path.isfile(self.record))
            planting.restore()
            self.assertFalse(os.path.lexists(self.record))
            self.assertFalse(os.path.exists(os.path.join(self.tree, ".taad")), "the folders the row made are removed")

    def test_a_file_that_is_not_awaited_is_still_plant_broken(self):
        self.put_record(b"{}\n")
        planting = harness.Planting(self.tree, harness.batches(self.rows)[0], set())
        planting.apply()
        self.assertEqual(len(planting.broken), 1)
        self.assertIn("already exists", next(iter(planting.broken.values())))
        self.assertEqual(self.read_record(), b"{}\n")


if __name__ == "__main__":
    unittest.main(verbosity=2)
