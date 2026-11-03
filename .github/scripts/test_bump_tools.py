import re
import unittest

import bump_tools as b


class BumpToolsTests(unittest.TestCase):
    def test_newest_holds_the_major_version(self):
        self.assertEqual(b.newest(["1.18.4", "2.0.0", "1.18.10"], hold_major=1), "1.18.10")
        self.assertEqual(b.newest(["1.18.4", "2.0.0"]), "2.0.0")
        self.assertIsNone(b.newest(["2.0.0"], hold_major=1))

    def test_four_part_versions_compare_as_numbers(self):
        self.assertEqual(b.newest(["1.0.0.78", "1.0.0.100", "1.0.0.9"]), "1.0.0.100")

    def test_reads_and_sets_args(self):
        text = "ARG MAKEMKV_VERSION=1.18.4\nARG DGDEMUX_VERSION=1.0.0.78\nFROM x\nARG MAKEMKV_VERSION\n"
        self.assertEqual(b.read_args(text), {"MAKEMKV_VERSION": "1.18.4", "DGDEMUX_VERSION": "1.0.0.78"})
        changed = b.set_arg(text, "DGDEMUX_VERSION", "1.0.0.80")
        self.assertIn("ARG DGDEMUX_VERSION=1.0.0.80\n", changed)
        self.assertIn("ARG MAKEMKV_VERSION\n", changed)

    def test_page_patterns(self):
        makemkv = next(t for t in b.TOOLS if t.arg == "MAKEMKV_VERSION")
        page = '<a href="makemkv-oss-1.18.4.tar.gz">x</a> <a href="makemkv-bin-2.0.0.tar.gz">'
        self.assertEqual(re.findall(makemkv.pattern, page), ["1.18.4", "2.0.0"])
        dgdemux = next(t for t in b.TOOLS if t.arg == "DGDEMUX_VERSION")
        self.assertEqual(re.findall(dgdemux.pattern, "dgdemux_linux_1.0.0.78.zip dgdemux_win_1.0.0.79.zip"), ["1.0.0.78"])

    def test_every_tool_has_an_arg_in_the_dockerfile(self):
        pins = b.read_args(b.DOCKERFILE.read_text())
        for tool in b.TOOLS:
            self.assertIn(tool.arg, pins, tool.name)


if __name__ == "__main__":
    unittest.main()
