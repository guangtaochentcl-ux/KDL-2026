# coding = utf8
import sys
import threading

from uiautodev.command_types import By

sys.path.append("../../")
import os
from common_api import waitTimeBack, selenium_single_object

os.path.abspath(".")

"""
    @Project:PycharmProjects
    @File:case1_gbs_Channel1andChannel2LogicOnOffStream.py.py
    @Author:十二点前要睡觉
    @Date:2023/9/21 14:06
"""


def loginWeb(seleniumObject):
    seleniumObject.toTxt("登录web端")
    seleniumObject.driver.find_element(By.XPATH,
                                       '//input[@type="text" and contains(@class, "form-control")]').send_keys("admin")
    seleniumObject.driver.find_element(By.XPATH,
                                       '//input[@type="password" and contains(@class, "form-control")]').send_keys(
        "justin")
    seleniumObject.driver.find_element(By.XPATH, '//span[contains(@class, "el-checkbox__label")]').click()
    seleniumObject.driver.find_element(By.ID, "btn-login").click()
    waitTimeBack(1)


def TEST_AREA(gbs_url):
    seleniumObject = selenium_single_object(gbs_url, log_name)
    seleniumObject.openUrl(seleniumObject.enter_url)
    waitTimeBack(3)
    loginWeb(seleniumObject)
    _cur_ip = gbs_url.replace("//", "/").split("/")[5]
    for j in range(0, 10000):
        # 拉流并获取对应按钮list
        playBtnsGet = seleniumObject.driver.find_elements(By.XPATH,
                                                          "//button[.//i[contains(@class, 'fa-play-circle')]]")
        playBtns = []
        print(f"{_cur_ip} 一共找到了 {len(playBtnsGet)} 个播放按钮")

        for i, btn in enumerate(playBtnsGet):
            print(f"{_cur_ip} 按钮 {i}: 显示状态={btn.is_displayed()}, 文本内容={btn.text}")
            if btn.is_displayed():
                playBtns.append(btn)
        playBtns[0].click()
        waitTimeBack(3)
        closePage_btn = seleniumObject.driver.find_elements(By.XPATH, "//button[contains(., '关闭')]")
        for i, btn in enumerate(closePage_btn):
            print(f"{_cur_ip} 按钮 {i}: 显示状态={btn.is_displayed()}, 文本内容={btn.text}")
            if btn.is_displayed():
                closePage_btn = btn
        closePage_btn.click()

        playBtns[1].click()
        waitTimeBack(5)
        closePage_btn.click()

        playBtns[2].click()
        waitTimeBack(5)
        closePage_btn.click()

        playBtns[3].click()
        waitTimeBack(5)
        closePage_btn.click()

        # stopBtnsGet = seleniumObject.driver.find_elements(By.XPATH, "//button[.//i[contains(@class, 'fa-stop')]]")
        # stopBtns = []
        # print(f"一共找到了 {len(stopBtnsGet)} 个播放按钮")
        # for i, btn in enumerate(stopBtnsGet):
        #     print(f"按钮 {i}: 显示状态={btn.is_displayed()}, 文本内容={btn.text}")
        #     if btn.is_displayed():
        #         stopBtns.append(btn)

        result_channel1 = False
        result_channel2 = False
        result_channel3 = False
        result_channel4 = False

        # 关闭通道1 -》打开通道1 -》获取当前FPS
        seleniumObject.toTxt(f"{_cur_ip} 通道1逻辑执行中……")
        # stopBtns[0].click()
        # seleniumObject.driver.find_element(By.XPATH, '/html/body/div[4]/div/div[3]/button[2]').click()
        # waitTimeBack(3)
        # 打开通道1
        playBtns[0].click()
        waitTimeBack(30)
        try:
            # 获取当前FPS：
            channel1_fps = int(seleniumObject.driver.find_element(By.XPATH,
                                                                  '//*[@id="pane-steam-info"]/div/div[4]').text.split(
                "fps")[0])
            seleniumObject.toTxt("{} 第{}次测试 - 当前视频流channel1 - FPS: ".format(_cur_ip, str(j)) + str(channel1_fps))
            if channel1_fps > 0:
                result_channel1 = True
            else:
                result_channel1 = False
        except Exception:
            result_channel1 = False
            break

        closePage_btn.click()

        # 关闭通道2 -》打开通道2 -》获取当前FPS
        seleniumObject.toTxt(f"{_cur_ip} 通道2逻辑执行中……")
        # stopBtns[1].click()
        # seleniumObject.driver.find_element(By.XPATH, '/html/body/div[4]/div/div[3]/button[2]').click()
        # waitTimeBack(3)
        # 打开通道2
        playBtns[1].click()
        waitTimeBack(30)
        try:
            # 获取当前FPS：
            channel2_fps = int(seleniumObject.driver.find_element(By.XPATH,
                                                                  '//*[@id="pane-steam-info"]/div/div[4]').text.split(
                "fps")[0])
            seleniumObject.toTxt("{} 第{}次测试 - 当前视频流channel2 - FPS: ".format(_cur_ip, str(j)) + str(channel2_fps))
            if channel2_fps > 0:
                result_channel2 = True
            else:
                result_channel2 = False
        except Exception:
            result_channel2 = False
            break

        closePage_btn.click()

        # 关闭通道3 -》打开通道3 -》获取当前FPS
        seleniumObject.toTxt(f"{_cur_ip} 通道3逻辑执行中……")
        # stopBtns[2].click()
        # seleniumObject.driver.find_element(By.XPATH, '/html/body/div[4]/div/div[3]/button[2]').click()
        # waitTimeBack(3)
        # 打开通道3
        playBtns[2].click()
        waitTimeBack(30)
        try:
            # 获取当前FPS：
            channel3_fps = int(seleniumObject.driver.find_element(By.XPATH,
                                                                  '//*[@id="pane-steam-info"]/div/div[4]').text.split(
                "fps")[0])
            seleniumObject.toTxt("{} 第{}次测试 - 当前视频流channel3 - FPS: ".format(_cur_ip, str(j)) + str(channel3_fps))
            if channel3_fps > 0:
                result_channel3 = True
            else:
                result_channel3 = False
        except Exception:
            result_channel3 = False
            break

        closePage_btn.click()

        # 关闭通道4 -》打开通道4 -》获取当前FPS
        seleniumObject.toTxt("通道4逻辑执行中……")
        # stopBtns[3].click()
        # seleniumObject.driver.find_element(By.XPATH, '/html/body/div[4]/div/div[3]/button[2]').click()
        # waitTimeBack(3)
        # 打开通道4
        playBtns[3].click()
        waitTimeBack(30)
        try:
            # 获取当前FPS：
            channel4_fps = int(seleniumObject.driver.find_element(By.XPATH,
                                                                  '//*[@id="pane-steam-info"]/div/div[4]').text.split(
                "fps")[0])
            seleniumObject.toTxt("第{}次测试 - 当前视频流channel4 - FPS: ".format(str(j)) + str(channel4_fps))
            if channel4_fps > 0:
                result_channel4 = True
            else:
                result_channel4 = False
        except Exception:
            result_channel4 = False
            break

        closePage_btn.click()

        allResult = False
        if result_channel1 and result_channel2 and result_channel3 and result_channel4:
            allResult = True
        else:
            allResult = False
        seleniumObject.toTxt("第{}次通道逻辑测试结果为：{}\n, 对应测试结果分别为：\n"
                             "通道1拉流结果：{}\n"
                             "通道2拉流结果：{}\n"
                             "通道3拉流结果：{}\n"
                             "通道4拉流结果：{}\n".format(j, allResult, result_channel1, result_channel2,
                                                         result_channel3, result_channel4))
        if not allResult:
            seleniumObject.toTxt("停止测试")
            break
        else:
            seleniumObject.toTxt("继续测试")
            waitTimeBack(3)


if __name__ == '__main__':
    """
        # 测试前先手动配置好GB28181的配置信息，确保能够拉流成功：
        SIP服务器地址：10.66.32.89
        SIP服务器端口：15060
        IPC设备ID：34020000001320000339
        
        # 如果无法运行，下载edgedriver放到脚本根目录下
        https://developer.microsoft.com/zh-cn/microsoft-edge/tools/webdriver/
    
        1、使用浏览器打开GBS平台，并打开通道一拉流
        2、打开通道二拉流
        3、关闭通道一拉流
        4、打开通道一拉流
        5、关闭通道二拉流
        6、打开通道二拉流
        7、3-6步骤测试500次
        备注：通道（channel0、channel1、channel2、channel3）
    """
    os.system("del *.log")
    gbs_url1 = "http://10.66.32.89:10000/#/devices/channels/34020000001320000111/1"
    gbs_url2 = "http://10.66.32.89:10000/#/devices/channels/34020000001320000222/1"
    # urlList = [gbs_url1]
    # gbs_url2 = "http://192.168.1.100/#/media_preview"
    urlList = [gbs_url1, gbs_url2]
    os.system("del *.log")
    waitTimeBack(3)
    log_name = "case1_gbs_Channel1andChannel2LogicOnOffStream"
    if len(urlList) == 1:
        # 单机模式
        gbs_url = urlList[0]
        print(
            "[{}] - 现在开始case1_gbs_Channel1andChannel2LogicOnOffStream测试，请耐心等待其跑完……".format(
                gbs_url))
        TEST_AREA(gbs_url, )
    elif len(urlList) >= 2:
        for gbs_url in urlList:
            # 多机测试模式
            print(gbs_url)
            t = threading.Thread(target=TEST_AREA, args=(gbs_url,))
            t.start()
