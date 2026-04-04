#!/bin/bash
# Test script for ANSI sequence testing

echo "Testing basic ANSI colors:"
echo -e "\033[31mRed\033[0m \033[32mGreen\033[0m \033[34mBlue\033[0m"

echo "Testing 256 colors:"
echo -e "\033[38;5;196mRed256\033[0m \033[38;5;46mGreen256\033[0m \033[38;5;21mBlue256\033[0m"

echo "Testing attributes:"
echo -e "\033[1mBold\033[0m \033[4mUnderline\033[0m \033[7mReverse\033[0m"

echo "Testing cursor movement:"
echo -n "Start"
echo -e "\033[3D\033[CRight"

echo "Done!"
