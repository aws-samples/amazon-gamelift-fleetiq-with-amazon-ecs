# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: MIT-0

import json
import datetime
import time
import os
import boto3
from boto3.dynamodb.conditions import Key, Attr
import redis
from datetime import timedelta

# The amount of seconds we give servers to start up
server_startup_grace_period = 60

cpu_per_task = 512
memory_per_task = 953

""" Returns the available memory and CPU for the whole ECS cluster """
def get_available_memory_and_cpu(ecs_cluster_name):
    total_cpu = 0
    total_memory = 0

    ecs = boto3.client("ecs")
    firstround = True
    nextToken = None

    # Use pagination to get all instances beyond the first 100 
    while firstround or nextToken != None:

        if nextToken == None:
            response = ecs.list_container_instances(
                cluster=ecs_cluster_name,
            )
        else:
            response = ecs.list_container_instances(
                cluster=ecs_cluster_name, nextToken=nextToken
            )
        print(response)
        if "nextToken" in response:
            print("found next token")
            nextToken = response["nextToken"]
        else:
            nextToken = None

        container_instances = response["containerInstanceArns"]
        if len(container_instances) > 0:

            # Get instance id, not full arn
            container_instances_id_only = []
            for arn in container_instances:
                container_instances_id_only.append(arn.split("/")[2])

            response = ecs.describe_container_instances(
                cluster=ecs_cluster_name,
                containerInstances=container_instances_id_only
            )

            for instance in response["containerInstances"]:
                for remaining_resource in instance["remainingResources"]:
                    if remaining_resource["name"] == "CPU":
                        #print("Remaining CPU: " + str(remaining_resource["integerValue"]))
                        total_cpu += int(remaining_resource["integerValue"])
                    elif remaining_resource["name"] == "MEMORY":
                        #print("Remaining Memoery: " + str(remaining_resource["integerValue"]))
                        total_memory += int(remaining_resource["integerValue"])

        firstround = False
            
        print("Subtotal: " + str(total_cpu) + "," + str(total_memory))

    return total_cpu, total_memory

def lambda_handler(event, context):

    print("Running scheduled Lambda function to start new game server tasks when necessary")

    # Get the resources from ECS and Task CLoudFormation Stacks from environment variables
    ecs_cluster_name = os.environ['ECS_CLUSTER_NAME'] 
    ecs_task_definition = ""

    cloudformation = boto3.client("cloudformation")
    # Get the Task to deploy (as this changes dynamically)
    stack = cloudformation.describe_stacks(StackName="fleetiq-game-servers-task-definition")["Stacks"][0]
    for output in stack["Outputs"]:
        print('%s=%s (%s)' % (output["OutputKey"], output["OutputValue"], output["Description"]))
        if output["OutputKey"] == "TaskDefinition":
            ecs_task_definition = output["OutputValue"]

    # Track start time
    start_time = time.time()

    ### Run the scaler up to 60 seconds (next one will be triggered after 1 minute)
    while (time.time() - start_time) < 59.0:

        try:
            # 1. Check Fleet CPU and Memory capacity
            total_cpu, total_memory = get_available_memory_and_cpu(ecs_cluster_name)
            print("Total CPU: " + str(total_cpu) + " Total Memory: " + str(total_memory))

            # 2. Check how many game server Tasks we can start
            max_tasks_based_on_cpu = int(total_cpu / cpu_per_task)
            max_tasks_based_on_memory = int(total_memory / memory_per_task)

            print("Total to start cpu: " + str(max_tasks_based_on_cpu) + " total to start mem: " + str(max_tasks_based_on_memory))

            # Start the lowest value of cpu and memory bound and max of 10 per round
            total_to_start = min(max_tasks_based_on_cpu, max_tasks_based_on_memory, 10)

            print("Will start: " + str(total_to_start))

            # Spin up the missing servers
            if total_to_start > 0:
                # Start a game server ECS Task for each missing game serve
                    client = boto3.client('ecs')
                    response = client.run_task(
                        cluster=ecs_cluster_name,
                        launchType = 'EC2',
                        taskDefinition=ecs_task_definition,
                        count = total_to_start
                    )
        except Exception as e:
            print("Exception occured in starting Tasks")
            print(e)
        # Wait for next round unless this was the last on this minute
        if time.time() - start_time < 59.0:
            print("Wait 1 second before next round")
            time.sleep(1.0)