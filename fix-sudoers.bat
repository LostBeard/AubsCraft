@echo off
ssh -t aubscraft "sudo cp /tmp/aubscraft_sudoers /etc/sudoers.d/aubscraft_admin && sudo chmod 440 /etc/sudoers.d/aubscraft_admin && sudo visudo -c && echo DONE"
pause
