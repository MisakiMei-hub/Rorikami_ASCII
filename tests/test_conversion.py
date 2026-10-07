"""Regressions for dark regions, watermark-only frames, and misplaced colors."""
import sys
from pathlib import Path
import unittest
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from build_ascii import HEIGHT, WIDTH, character_lut, convert_cells, matte_candidate


class ConversionTests(unittest.TestCase):
    def test_dark_picture_is_not_lost_as_blank_characters(self):
        rgb = np.full((HEIGHT, WIDTH, 3), 16, np.uint8)
        rgb[:, WIDTH // 2:] = 160
        cells, _ = convert_cells(rgb, character_lut()[0])
        self.assertGreater((cells[:, :WIDTH // 2, 0] != ord(' ')).mean(), 0.95)

    def test_corner_logo_alone_does_not_define_picture_bounds(self):
        rgb = np.zeros((180, 320, 3), np.uint8)
        rgb[6:19, 238:320] = 255
        self.assertIsNone(matte_candidate(rgb))

    def test_watermark_does_not_move_top_matte(self):
        rgb = np.zeros((180, 320, 3), np.uint8)
        rgb[18:162] = (144, 112, 80)
        rgb[6:19, 238:320] = 255
        self.assertEqual(matte_candidate(rgb).tolist(), [0, 18, 320, 162])

    def test_skin_and_navy_stay_in_their_own_character_cells(self):
        rgb = np.empty((HEIGHT, WIDTH, 3), np.uint8)
        rgb[:, :WIDTH // 2] = (240, 184, 136)
        rgb[:, WIDTH // 2:] = (32, 48, 112)
        cells, _ = convert_cells(rgb, character_lut()[0])
        skin = cells[HEIGHT // 2, WIDTH // 4, 1:]
        navy = cells[HEIGHT // 2, WIDTH * 3 // 4, 1:]
        self.assertTrue(skin[0] > skin[1] > skin[2])
        self.assertTrue(navy[2] > navy[1] > navy[0])
        self.assertGreater(int(skin[0]), 200)
        self.assertLess(int(navy[0]), 80)


if __name__ == '__main__': unittest.main()
